using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;

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

    public async Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct)
    {
        if (!prioritizeRequests) return await ListRequestsByDateAsync(filters, cursor, ct);
        var position = ReadRequestCursor(cursor);
        var rank = position.Priority;
        var start = position.Key?.ToDictionary(pair => pair.Key, pair => S(pair.Value));
        var items = new List<ManagedRequest>();
        var evaluated = 0;
        while (rank >= 0 && evaluated < 25)
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
                Limit = 25 - evaluated
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
}
