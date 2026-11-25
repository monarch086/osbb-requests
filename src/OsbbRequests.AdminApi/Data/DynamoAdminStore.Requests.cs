using OsbbRequests.Services;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;

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
                key.GetValueOrDefault("requestPriorityCreatedAt")?.StartsWith(cursor.Priority.ToString(CultureInfo.InvariantCulture) + "#", StringComparison.Ordinal) != true ||
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
        var hasDateFilter = HasRequestDateFilter(filters);
        while (rank >= 0 && evaluated < limit)
        {
            var query = CreateRequestQuery(filters, rank, start);
            query.Limit = limit - evaluated;
            var page = await db.QueryAsync(query, ct);
            items.AddRange((page.Items ?? []).Select(Managed));
            evaluated += page.ScannedCount ?? page.Items?.Count ?? 0;
            if (page.LastEvaluatedKey is { Count: > 0 } key)
            {
                var lastRank = key["requestPriorityCreatedAt"].S[0] - '0';
                return new(items, RequestPageCursor(new(building, lastRank, key.ToDictionary(pair => pair.Key, pair => pair.Value.S))));
            }
            rank = hasDateFilter ? rank - 1 : -1;
            start = null;
        }
        return new(items, rank < 0 ? null : RequestPageCursor(new(building, rank, null)));
    }

    public async Task<AdminRequestPage> ListRequestsWithStatisticsAsync(RequestFilters filters, string? cursor, int pageNumber, CancellationToken ct, bool includeCounts = true)
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

        RequestCounts? counts = null;
        if (includeCounts)
        {
            // An exhausted first page contains every match, including when the filter returns no rows.
            counts = string.IsNullOrEmpty(cursor) && next is null
                ? new(items.Count, items.Count(item => item.Request.Status == RequestStatus.New),
                    items.Count(item => item.Request.Status == RequestStatus.InProgress))
                : await CountRequestsAsync(filters, ct);
        }
        return new(items, next, pageNumber, 25, counts);
    }

    private async Task<RequestCounts> CountRequestsAsync(RequestFilters filters, CancellationToken ct)
    {
        var total = 0;
        var newCount = 0;
        var progressCount = 0;
        var lastRank = prioritizeRequests && HasRequestDateFilter(filters) ? 0 : 3;
        for (var rank = 3; rank >= lastRank; rank--)
        {
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var query = CreateRequestQuery(filters, rank, start);
                query.ProjectionExpression = "payload";
                var result = await db.QueryAsync(query, ct);
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

    private static bool HasRequestDateFilter(RequestFilters filters) => filters.From is not null || filters.To is not null;

    private QueryRequest CreateRequestQuery(RequestFilters filters, int rank, Dictionary<string, AttributeValue>? start)
    {
        var values = new Dictionary<string, AttributeValue> { [":building"] = S(building) };
        var condition = "requestBuildingId = :building";
        if (HasRequestDateFilter(filters))
        {
            var prefix = prioritizeRequests ? rank.ToString(CultureInfo.InvariantCulture) + "#" : "";
            var sortKey = prioritizeRequests ? "requestPriorityCreatedAt" : "requestCreatedAt";
            values[":lower"] = S(prefix + (filters.From ?? "0000"));
            values[":upper"] = S(prefix + (filters.To ?? "9999") + "~");
            condition += " AND " + sortKey + " BETWEEN :lower AND :upper";
        }
        else if (prioritizeRequests && rank < 3 && start is null)
        {
            // Older cursors can point to the next priority bucket without an evaluated key.
            values[":upper"] = S((rank + 1).ToString(CultureInfo.InvariantCulture) + "#");
            condition += " AND requestPriorityCreatedAt < :upper";
        }
        return new QueryRequest
        {
            TableName = dataTable,
            IndexName = prioritizeRequests ? "BuildingPriorityRequestsIndex" : "BuildingRequestsIndex",
            KeyConditionExpression = condition,
            ExpressionAttributeValues = values,
            FilterExpression = RequestFilterExpression(filters, values),
            ExclusiveStartKey = start,
            ScanIndexForward = false
        };
    }
}
