using System.Diagnostics;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;

namespace OsbbRequests.AdminApi.Statistics;

public sealed record DailyStatistic(string Date, string Endpoint, long Requests, double ReadUnits, double WriteUnits, double DurationMs);
public interface IStatisticsStore
{
    Task<IReadOnlyList<DailyStatistic>> ReadAsync(int days, CancellationToken ct);
    Task RecordAsync(string endpoint, double read, double write, long durationMs, CancellationToken ct);
}

public sealed class StatisticsStore(IAmazonDynamoDB db, string table) : IStatisticsStore
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

public sealed class RequestMeasurement
{
    private readonly object sync = new();
    private double read;
    private double write;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public void Add(ConsumedCapacity? capacity, bool writeOperation = false)
    {
        if (capacity is null) return;
        lock (sync)
        {
            if (capacity.ReadCapacityUnits is null && capacity.WriteCapacityUnits is null)
            {
                if (writeOperation) write += capacity.CapacityUnits ?? 0;
                else read += capacity.CapacityUnits ?? 0;
            }
            else { read += capacity.ReadCapacityUnits ?? 0; write += capacity.WriteCapacityUnits ?? 0; }
        }
    }
    public (double Read, double Write, long Duration) Finish() { lock (sync) return (read, write, clock.ElapsedMilliseconds); }
    public static string? Endpoint(APIGatewayHttpApiV2ProxyRequest request)
    {
        var method = request.RequestContext?.Http?.Method;
        var path = request.RawPath?.TrimEnd('/') ?? "";
        if (method is not ("GET" or "POST" or "PATCH") || !path.StartsWith("/admin/", StringComparison.Ordinal)) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resource = parts.Length > 1 ? parts[1] : "other";
        if (resource is not ("requests" or "residents" or "registrations" or "administrators" or "assignees" or "messages" or "me" or "statistics" or "apartment-reviews")) resource = "other";
        var suffix = parts.Length == 3 && parts[2] is "directory" or "export" or "keys" or "count" ? "/" + parts[2] : parts.Length > 2 ? "/{id}" : "";
        return method + " /admin/" + resource + suffix;
    }
}

public sealed class MeteredDynamoDb : AmazonDynamoDBClient
{
    public static readonly AsyncLocal<RequestMeasurement?> Measurement = new();
    public override async Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
    { request.ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES; var result = await base.QueryAsync(request, cancellationToken); Measurement.Value?.Add(result.ConsumedCapacity); return result; }
    public override async Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
    { request.ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL; var result = await base.GetItemAsync(request, cancellationToken); Measurement.Value?.Add(result.ConsumedCapacity); return result; }
    public override async Task<BatchGetItemResponse> BatchGetItemAsync(BatchGetItemRequest request, CancellationToken cancellationToken = default)
    { request.ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL; var result = await base.BatchGetItemAsync(request, cancellationToken); foreach (var item in result.ConsumedCapacity ?? []) Measurement.Value?.Add(item); return result; }
    public override async Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
    { request.ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES; var result = await base.TransactWriteItemsAsync(request, cancellationToken); foreach (var item in result.ConsumedCapacity ?? []) Measurement.Value?.Add(item, true); return result; }
}
