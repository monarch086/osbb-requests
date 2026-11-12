using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    private sealed record RequestCursor(string ComplexId, int Priority, Dictionary<string, string>? Key);
    private RequestCursor ReadRequestCursor(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new(building, 3, null);
        try
        {
            if (value.Length > 3000) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<RequestCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            if (cursor is null || cursor.ComplexId != building || cursor.Priority is < 0 or > 3) throw new FormatException();
            if (cursor.Key is { } key && (key.Count != 4 || key.GetValueOrDefault("requestBuildingId") != building ||
                key.GetValueOrDefault("requestPriorityCreatedAt")?.StartsWith(cursor.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture) + "#", StringComparison.Ordinal) != true ||
                key.GetValueOrDefault("pk")?.StartsWith("USER#", StringComparison.Ordinal) != true ||
                key.GetValueOrDefault("sk")?.StartsWith("REQUEST#", StringComparison.Ordinal) != true)) throw new FormatException();
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ApiError(400, "Некоректна сторінка. Оновіть список заявок."); }
    }
    private static string RequestPageCursor(RequestCursor value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct) =>
        ListRequestsPageAsync(filters, cursor, 25, ct);

    private async Task<Page<ManagedRequest>> ListRequestsPageAsync(RequestFilters filters, string? cursor, int limit, CancellationToken ct)
    {
        if (!prioritizeRequests) return await ListRequestsByDateAsync(filters, cursor, limit, ct);
        var position = ReadRequestCursor(cursor);
        var rank = position.Priority;
        var start = position.Key?.ToDictionary(pair => pair.Key, pair => S(pair.Value));
        var items = new List<ManagedRequest>();
        var evaluated = 0;
        while (rank >= 0 && evaluated < limit)
        {
            var prefix = rank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "#";
            var values = new Dictionary<string, AttributeValue>
            {
                [":building"] = S(building),
                [":lower"] = S(prefix + (filters.From ?? "0000")),
                [":upper"] = S(prefix + (filters.To ?? "9999") + "~")
            };
            var filter = RequestFilterExpression(filters, values);
            var page = await db.QueryAsync(new QueryRequest
            {
                TableName = dataTable,
                IndexName = "BuildingPriorityRequestsIndex",
                KeyConditionExpression = "requestBuildingId = :building AND requestPriorityCreatedAt BETWEEN :lower AND :upper",
                ExpressionAttributeValues = values,
                FilterExpression = filter,
                ExclusiveStartKey = start,
                ScanIndexForward = false,
                Limit = limit - evaluated
            }, ct);
            items.AddRange((page.Items ?? []).Select(Managed));
            evaluated += page.ScannedCount ?? page.Items?.Count ?? 0;
            if (page.LastEvaluatedKey is { Count: > 0 })
                return new(items, RequestPageCursor(new(building, rank, page.LastEvaluatedKey.ToDictionary(pair => pair.Key, pair => pair.Value.S))));
            rank--;
            start = null;
        }
        return new(items, rank < 0 ? null : RequestPageCursor(new(building, rank, null)));
    }

    public async Task<AdminRequestPage> ListRequestsWithStatisticsAsync(RequestFilters filters, string? cursor, int pageNumber, CancellationToken ct)
    {
        if (pageNumber < 1 || pageNumber > 1000000) throw new ApiError(400, "Некоректна сторінка.");
        var items = new List<ManagedRequest>();
        var next = cursor;
        do
        {
            var page = await ListRequestsPageAsync(filters, next, 25 - items.Count, ct);
            items.AddRange(page.Items);
            if (page.Cursor is not null && page.Cursor == next) throw new InvalidOperationException("Repeated request cursor.");
            next = page.Cursor;
        } while (items.Count < 25 && next is not null);

        var counts = await CountRequestsAsync(filters, ct);
        return new(items, next, pageNumber, 25, counts);
    }

    private async Task<RequestCounts> CountRequestsAsync(RequestFilters filters, CancellationToken ct)
    {
        var total = 0;
        var newCount = 0;
        var progressCount = 0;
        var firstRank = prioritizeRequests ? 3 : 0;
        for (var rank = firstRank; rank >= 0; rank--)
        {
            var values = new Dictionary<string, AttributeValue> { [":building"] = S(building) };
            string condition;
            if (prioritizeRequests)
            {
                var prefix = rank.ToString(System.Globalization.CultureInfo.InvariantCulture) + "#";
                values[":lower"] = S(prefix + (filters.From ?? "0000"));
                values[":upper"] = S(prefix + (filters.To ?? "9999") + "~");
                condition = "requestBuildingId = :building AND requestPriorityCreatedAt BETWEEN :lower AND :upper";
            }
            else
            {
                condition = "requestBuildingId = :building";
                if (filters.From is not null || filters.To is not null)
                {
                    values[":from"] = S(filters.From ?? "0000");
                    values[":to"] = S((filters.To ?? "9999") + "~");
                    condition += " AND requestCreatedAt BETWEEN :from AND :to";
                }
            }
            var filter = RequestFilterExpression(filters, values);
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var result = await db.QueryAsync(new QueryRequest
                {
                    TableName = dataTable,
                    IndexName = prioritizeRequests ? "BuildingPriorityRequestsIndex" : "BuildingRequestsIndex",
                    KeyConditionExpression = condition,
                    ExpressionAttributeValues = values,
                    FilterExpression = filter,
                    ProjectionExpression = "payload",
                    ExclusiveStartKey = start
                }, ct);
                foreach (var row in result.Items ?? [])
                {
                    var status = Payload<MaintenanceRequest>(row).Status;
                    total++;
                    if (status == RequestStatus.New) newCount++;
                    else if (status == RequestStatus.InProgress) progressCount++;
                }
                start = result.LastEvaluatedKey;
            } while (start is { Count: > 0 });
        }
        return new(total, newCount, progressCount);
    }
}
