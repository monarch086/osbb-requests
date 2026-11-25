using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace OsbbRequests.Aws;

public sealed class DynamoStatisticsStore(IAmazonDynamoDB db, string table) : IStatisticsStore
{
    public async Task RecordAsync(string endpoint, double read, double write, long durationMs, CancellationToken ct)
    {
        var key = new Dictionary<string, AttributeValue> { ["pk"] = new("API"), ["sk"] = new(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "#" + endpoint) };
        await db.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD requests :one, readUnits :read, writeUnits :write, durationMs :duration",
            ExpressionAttributeValues = new()
            {
                [":one"] = new() { N = "1" },
                [":read"] = new() { N = read.ToString("R", CultureInfo.InvariantCulture) },
                [":write"] = new() { N = write.ToString("R", CultureInfo.InvariantCulture) },
                [":duration"] = new() { N = durationMs.ToString(CultureInfo.InvariantCulture) }
            }
        }, ct);
    }
    public async Task<IReadOnlyList<DailyStatistic>> ReadAsync(int days, CancellationToken ct)
    {
        var start = DateTime.UtcNow.Date.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTime.UtcNow.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "#~";
        var list = new List<DailyStatistic>();
        Dictionary<string, AttributeValue>? cursor = null;
        do
        {
            var result = await db.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk AND sk BETWEEN :start AND :end",
                ExpressionAttributeValues = new() { [":pk"] = new("API"), [":start"] = new(start), [":end"] = new(end) },
                ExclusiveStartKey = cursor
            }, ct);
            foreach (var item in result.Items ?? [])
            {
                var sk = item["sk"].S;
                static double Number(Dictionary<string, AttributeValue> row, string name) => double.Parse(row[name].N, CultureInfo.InvariantCulture);
                list.Add(new(sk[..10], sk[11..], (long)Number(item, "requests"), Number(item, "readUnits"), Number(item, "writeUnits"), Number(item, "durationMs")));
            }
            cursor = result.LastEvaluatedKey;
        } while (cursor is { Count: > 0 });
        return list;
    }
}
