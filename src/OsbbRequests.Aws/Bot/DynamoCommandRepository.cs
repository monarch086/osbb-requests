using OsbbRequests.CommandProcessor.Persistence;
using OsbbRequests.Domain.Models;
using OsbbRequests.Domain;
using OsbbRequests.Shared.Residents;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.CommandProcessor.Models;

namespace OsbbRequests.Aws.Bot;

public sealed partial class DynamoCommandRepository(IAmazonDynamoDB client, string tableName,
    TimeProvider? timeProvider = null, string? residentsTable = null, string? complexId = null, bool usePhoneIndex = false) : ICommandRepository
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string RequestKey(long id) => "REQUEST#" + id.ToString("D20", CultureInfo.InvariantCulture);
    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new()
    {
        ["pk"] = new AttributeValue(pk),
        ["sk"] = new AttributeValue(sk)
    };

    private async Task<Dictionary<string, AttributeValue>> GetAsync(
        string pk, string sk, CancellationToken cancellationToken)
    {
        var response = await client.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Key(pk, sk),
            ConsistentRead = true
        }, cancellationToken);
        return response.Item ?? [];
    }

    public async Task<long?> GetNotificationRequestAsync(long userId, long messageId, CancellationToken ct)
    {
        var item = complexId is null ? [] : await GetAsync(ResidentNotification.Partition(complexId), ResidentNotification.MessageKey(userId, messageId), ct);
        if (item.Count == 0) item = await GetAsync("USER#" + Number(userId), "NOTIFICATION_MESSAGE#" + Number(messageId), ct);
        return item.TryGetValue("requestId", out var id) && long.TryParse(id.N, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    public async Task<DeletableRequest?> GetDeletableRequestAsync(long userId, long requestId, CancellationToken ct)
    {
        var item = await GetAsync("USER#" + Number(userId), RequestKey(requestId), ct);
        if (!item.TryGetValue("payload", out var payload)) return null;
        var request = JsonSerializer.Deserialize<MaintenanceRequest>(payload.S)
            ?? throw new InvalidOperationException("Invalid request data.");
        return new(request.Id, request.OwnerId, request.BuildingId, request.Status,
            int.Parse(item["version"].N, CultureInfo.InvariantCulture));
    }

    public async Task<PublicRequestPage> ListPublicRequestsAsync(string complexId, string? before, CancellationToken ct)
    {
        var values = new Dictionary<string, AttributeValue>
        {
            [":complex"] = new(complexId),
            [":new"] = new("New"),
            [":progress"] = new("InProgress")
        };
        var condition = "requestBuildingId = :complex";
        if (before is not null) { condition += " AND requestCreatedAt < :before"; values[":before"] = new(before); }
        const int pageSize = 5;
        const int maxQueries = 25;
        var items = new List<PublicRequestSummary>();
        Dictionary<string, AttributeValue>? startKey = null;
        string? next = null;
        for (var queryNumber = 0; queryNumber < maxQueries; queryNumber++)
        {
            var page = await client.QueryAsync(new QueryRequest
            {
                TableName = tableName,
                IndexName = "BuildingRequestsIndex",
                KeyConditionExpression = condition,
                FilterExpression = "requestStatus IN (:new, :progress)",
                ExpressionAttributeValues = values,
                ScanIndexForward = false,
                Limit = pageSize + 1 - items.Count,
                ProjectionExpression = "pk, sk, requestCreatedAt",
                ExclusiveStartKey = startKey
            }, ct);
            foreach (var candidate in page.Items ?? [])
            {
                // The GSI can lag behind privacy/status updates and deletion. Recheck the current base item.
                var item = await GetAsync(candidate["pk"].S, candidate["sk"].S, ct);
                if (!item.TryGetValue("payload", out var payload)) continue;
                var request = JsonSerializer.Deserialize<MaintenanceRequest>(payload.S);
                if (request is null || request.BuildingId != complexId || request.Visibility != RequestVisibility.Public || request.Category == RequestCategories.KeyOrder ||
                    request.Status is not (RequestStatus.New or RequestStatus.InProgress)) continue;
                items.Add(new(request.Id, request.Status, request.CreatedAt, request.Scope, request.BuildingNumber,
                    request.Floor, request.Category, request.Location, request.Description, request.Entrance, candidate.GetValueOrDefault("requestCreatedAt")?.S));
            }
            if (items.Count > pageSize)
            {
                var cursor = items[pageSize - 1].CursorKey ?? throw new InvalidOperationException("Missing public request cursor.");
                return new(items.Take(pageSize).ToArray(), cursor);
            }
            if (page.LastEvaluatedKey is not { Count: > 0 } lastKey) return new(items, null);
            var last = lastKey.GetValueOrDefault("requestCreatedAt")?.S;
            if (string.IsNullOrEmpty(last) || next == last || before == last)
                throw new InvalidOperationException("Invalid public request cursor.");
            next = last;
            startKey = lastKey;
        }
        return new(items, next);
    }

    public async Task<PageNavigation?> GetPageNavigationAsync(long userId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("USER#" + Number(userId), "PAGE_NAVIGATION", cancellationToken);
        return item.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<PageNavigation>(payload.S) : null;
    }

    public async Task<RequestLookup?> GetRequestLookupAsync(long userId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("USER#" + Number(userId), "REQUEST_LOOKUP", cancellationToken);
        return item.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<RequestLookup>(payload.S) : null;
    }

    public async Task<RequestDraft?> GetRequestDraftAsync(long userId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("USER#" + Number(userId), "REQUEST_DRAFT", cancellationToken);
        return item.TryGetValue("payload", out var payload) ? JsonSerializer.Deserialize<RequestDraft>(payload.S) : null;
    }

    public async Task<ResidentRegistration?> GetRegistrationAsync(long userId, CancellationToken cancellationToken)
    {
        if (residentsTable is not null && complexId is not null)
        {
            var record = await GetResidentRecordAsync(complexId, "USER#" + Number(userId), "REGISTRATION", cancellationToken);
            var resident = record.TryGetValue("payload", out var data) ? JsonSerializer.Deserialize<ResidentRegistration>(data.S) : null;
            return resident?.UserId == userId && resident.BuildingId == complexId ? resident : null;
        }
        var item = await GetAsync("USER#" + Number(userId), "REGISTRATION", cancellationToken);
        return item.TryGetValue("payload", out var payload)
            ? JsonSerializer.Deserialize<ResidentRegistration>(payload.S) : null;
    }

    public async Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken cancellationToken)
    {
        if (complexId is not null && buildingId != complexId) return false;
        var item = residentsTable is not null && complexId is not null
            ? await GetResidentRecordAsync(buildingId, "USER#" + Number(userId), "MEMBERSHIP", cancellationToken)
            : await GetAsync("USER#" + Number(userId), "MEMBERSHIP", cancellationToken);
        return item.TryGetValue("buildingId", out var building) && building.S == buildingId &&
            item.TryGetValue("active", out var active) && active.BOOL == true;
    }

    public async Task<MaintenanceRequest?> GetRequestAsync(long userId, long requestId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("USER#" + Number(userId), RequestKey(requestId), cancellationToken);
        return item.TryGetValue("payload", out var payload)
            ? JsonSerializer.Deserialize<MaintenanceRequest>(payload.S) : null;
    }

    public async Task<RequestPage> ListRequestsAsync(long userId, long? beforeId, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, AttributeValue>
        {
            [":pk"] = new AttributeValue("USER#" + Number(userId)),
            [":prefix"] = new AttributeValue("REQUEST#")
        };
        var response = await client.QueryAsync(new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = values,
            ExclusiveStartKey = beforeId.HasValue
                ? Key("USER#" + Number(userId), RequestKey(beforeId.Value)) : null,
            ScanIndexForward = false,
            ConsistentRead = true,
            Limit = 11
        }, cancellationToken);
        var requests = (response.Items ?? []).Select(item =>
            JsonSerializer.Deserialize<MaintenanceRequest>(item["payload"].S)
                ?? throw new InvalidOperationException("Invalid request data.")).ToList();
        var items = requests.Take(10).ToList();
        return new RequestPage(items,
            requests.Count > 10 || response.LastEvaluatedKey is { Count: > 0 } && items.Count > 0 ? items[^1].Id : null);
    }

    public async Task<StoredReply?> GetReplyAsync(long updateId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("UPDATE#" + Number(updateId), "REPLY", cancellationToken);
        if (!item.TryGetValue("payload", out var payload))
            return null;
        var reply = JsonSerializer.Deserialize<StoredReply>(payload.S)
            ?? throw new InvalidOperationException("Invalid reply data.");
        return reply with { Delivered = item.TryGetValue("delivered", out var delivered) && delivered.BOOL == true };
    }

    public async Task MarkDeliveredAsync(long updateId, CancellationToken cancellationToken) =>
        await client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = Key("UPDATE#" + Number(updateId), "REPLY"),
            UpdateExpression = "SET delivered = :delivered",
            ConditionExpression = "attribute_exists(pk)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":delivered"] = new AttributeValue { BOOL = true }
            }
        }, cancellationToken);

    private static Dictionary<string, AttributeValue> RegistrationItem(string pk, string sk, ResidentRegistration value)
    {
        var item = Key(pk, sk);
        item["payload"] = new AttributeValue(JsonSerializer.Serialize(value));
        item["version"] = new AttributeValue { N = value.Version.ToString(CultureInfo.InvariantCulture) };
        return item;
    }

    private TransactWriteItem NotificationWrite(ResidentNotification notification)
    {
        var item = Key(ResidentNotification.Partition(notification.ComplexId), ResidentNotification.DueKey(notification.CreatedAt, notification.Id));
        item["payload"] = new(JsonSerializer.Serialize(notification));
        item["attempts"] = new() { N = "0" };
        return Put(item);
    }

    private TransactWriteItem Put(Dictionary<string, AttributeValue> item) => new()
    {
        Put = new Put { TableName = tableName, Item = item, ConditionExpression = "attribute_not_exists(pk)" }
    };
}
