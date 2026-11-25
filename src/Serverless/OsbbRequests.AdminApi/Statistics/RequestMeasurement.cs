using System.Diagnostics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;

namespace OsbbRequests.AdminApi.Statistics;

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
