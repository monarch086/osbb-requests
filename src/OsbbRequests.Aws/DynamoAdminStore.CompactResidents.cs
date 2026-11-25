using OsbbRequests.Services;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Aws;

public sealed partial class DynamoAdminStore
{
    private const int ResidentPageSize = 25;
    private sealed record ResidentCursor(string Group, bool Ascending, string? Type, int Offset, string RowKey, string Order, bool ByName = false);

    public async Task<CompactResidentPage?> ListCompactResidentsAsync(int page, string? buildingNumber, string? residentType,
        string? direction, string? cursor, CancellationToken ct, bool byName = false)
    {
        if (!compactResidentDirectory || byName && !compactResidentNameDirectory) return null;
        if (page is < 1 or > 1_000_000) throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректна сторінка.");
        ValidateResidentFilters(buildingNumber, residentType, "registeredAt", direction);
        var table = residentsTable ?? throw new InvalidOperationException("Residents table is not configured.");

        var kind = string.IsNullOrEmpty(buildingNumber) ? "ALL" : "BUILDING";
        var scope = kind == "ALL" ? kind : kind + "#" + buildingNumber;
        var prefix = "COMPLEX#" + building + "#";
        var group = prefix + "APPROVED#" + scope;
        var rowPrefix = prefix + "DIRECTORY_ROW#" + kind + "#";
        var ascending = direction == "asc";
        var sortKey = byName ? "listNameOrder" : "listOrder";
        var index = byName ? "ResidentDirectoryByName" : "ResidentDirectoryByDate";
        if (!ascending)
        {
            sortKey += "Descending";
            index += "Descending";
        }
        var type = string.IsNullOrEmpty(residentType) ? null : residentType;

        var counters = await Get(table, new() { ["pk"] = S(prefix + "DIRECTORY_COUNT#" + scope) }, ct);
        if (byName && counters?.GetValueOrDefault("nameOrderUnsupported")?.BOOL == true) return null;
        int Count(ResidentType resident) => int.Parse(counters?.GetValueOrDefault(resident.ToString())?.N ?? "0", CultureInfo.InvariantCulture);
        var owners = Count(ResidentType.Owner);
        var family = Count(ResidentType.FamilyMember);
        var tenants = Count(ResidentType.Tenant);
        var employees = Count(ResidentType.Employee);
        var counts = new ResidentCounts(owners + family + tenants + employees, owners, family, tenants, employees);
        var total = type is null ? counts.Total : Count(Enum.Parse<ResidentType>(type));
        var actualPage = Math.Min(page, Math.Max(1, (total + ResidentPageSize - 1) / ResidentPageSize));
        if (total == 0) return new([], 1, ResidentPageSize, 0, counts, null, null);

        var target = (actualPage - 1) * ResidentPageSize;
        var anchor = ReadResidentCursor(cursor, group, ascending, type, rowPrefix, byName);
        if (anchor?.Offset > target) anchor = null;
        var offset = anchor?.Offset ?? 0;
        var start = anchor is null ? null : new Dictionary<string, AttributeValue>
        {
            ["pk"] = S(anchor.RowKey),
            ["listGroup"] = S(group),
            [sortKey] = byName ? new() { B = new MemoryStream(Convert.FromBase64String(anchor.Order)) } : S(anchor.Order)
        };

        Task<QueryResponse> QueryAsync(int limit, bool countOnly)
        {
            var query = new QueryRequest
            {
                TableName = table,
                IndexName = index,
                KeyConditionExpression = "listGroup = :group",
                ExpressionAttributeValues = new() { [":group"] = S(group) },
                ExclusiveStartKey = start,
                Limit = limit
            };
            if (countOnly) query.Select = Select.COUNT;
            if (type is not null)
            {
                query.FilterExpression = "residentType = :type";
                query.ExpressionAttributeValues[":type"] = S(type);
            }
            return db.QueryAsync(query, ct);
        }

        string? CursorAt(int position) => start is { Count: > 0 }
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ResidentCursor(group, ascending, type, position, start["pk"].S,
                byName ? Convert.ToBase64String(start[sortKey].B.ToArray()) : start[sortKey].S, byName)))
            : null;

        void Advance(QueryResponse result)
        {
            var next = result.LastEvaluatedKey;
            if (next is { Count: > 0 } && start?.GetValueOrDefault("pk")?.S == next["pk"].S)
                throw new InvalidOperationException("Repeated resident directory cursor.");
            start = next;
        }

        void ResumeAfter(Dictionary<string, AttributeValue> row) =>
            start = new() { ["pk"] = row["pk"], ["listGroup"] = S(group), [sortKey] = row[sortKey] };

        while (offset < target)
        {
            var remaining = target - offset;
            var result = await QueryAsync(type is null ? Math.Min(remaining, 250) : 100, countOnly: type is null);
            var matches = result.Items ?? [];
            if (type is not null && matches.Count > remaining)
            {
                ResumeAfter(matches[remaining - 1]);
                break;
            }
            offset += type is null ? result.Count ?? 0 : matches.Count;
            Advance(result);
            if (start is not { Count: > 0 }) return new([], actualPage, ResidentPageSize, total, counts, null, null);
        }

        var startCursor = CursorAt(target);
        var items = new List<ResidentListRow>(ResidentPageSize);
        do
        {
            var remaining = ResidentPageSize - items.Count;
            var result = await QueryAsync(type is null ? remaining : 100, countOnly: false);
            var matches = result.Items ?? [];
            foreach (var item in matches.Take(remaining))
            {
                string? Value(string key) => item.GetValueOrDefault(key)?.S;
                items.Add(new(item["registrationId"].S, Value("firstName"), Value("lastName"),
                    Enum.Parse<ResidentType>(item["residentType"].S),
                    Value("buildingNumber") is "Unspecified" ? null : Value("buildingNumber"), Value("apartmentNumber")));
            }
            if (matches.Count > remaining) ResumeAfter(matches[remaining - 1]);
            else Advance(result);
        } while (items.Count < ResidentPageSize && start is { Count: > 0 });

        return new(items, actualPage, ResidentPageSize, total, counts, startCursor,
            items.Count == ResidentPageSize && target + items.Count < total ? CursorAt(target + items.Count) : null);
    }

    private static ResidentCursor? ReadResidentCursor(string? cursor, string group, bool ascending, string? type, string rowPrefix, bool byName)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try
        {
            if (cursor.Length > 4096) throw new FormatException();
            var value = JsonSerializer.Deserialize<ResidentCursor>(Convert.FromBase64String(cursor));
            if (value is null || value.Group != group || value.Ascending != ascending || value.Type != type || value.ByName != byName ||
                value.Offset <= 0 || value.Offset % ResidentPageSize != 0 ||
                value.RowKey?.StartsWith(rowPrefix, StringComparison.Ordinal) != true ||
                !long.TryParse(value.RowKey.AsSpan(rowPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0 ||
                !ValidResidentOrder(value.Order, byName))
                throw new FormatException();
            return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректна сторінка. Оновіть список мешканців.");
        }
    }
    private static bool ValidResidentOrder(string? order, bool byName)
    {
        if (byName) return order is { Length: > 0 } && Convert.FromBase64String(order).Length is >= 33 and <= 1024;
        return order is { Length: 52 } && order[19] == '#' &&
            long.TryParse(order.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            order[20..].All(Uri.IsHexDigit);
    }
}
