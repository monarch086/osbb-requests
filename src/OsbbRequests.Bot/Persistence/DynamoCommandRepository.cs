using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Persistence;

public sealed partial class DynamoCommandRepository(IAmazonDynamoDB client, string tableName,
    TimeProvider? timeProvider = null) : ICommandRepository
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
        var page = await client.QueryAsync(new QueryRequest
        {
            TableName = tableName,
            IndexName = "BuildingRequestsIndex",
            KeyConditionExpression = condition,
            FilterExpression = "requestStatus IN (:new, :progress)",
            ExpressionAttributeValues = values,
            ScanIndexForward = false,
            Limit = 5,
            ProjectionExpression = "pk, sk"
        }, ct);
        var items = new List<PublicRequestSummary>();
        foreach (var candidate in page.Items ?? [])
        {
            // The GSI can lag behind privacy/status updates and deletion. Recheck the current base item.
            var item = await GetAsync(candidate["pk"].S, candidate["sk"].S, ct);
            if (!item.TryGetValue("payload", out var payload)) continue;
            var request = JsonSerializer.Deserialize<MaintenanceRequest>(payload.S);
            if (request is null || request.BuildingId != complexId || request.Visibility != RequestVisibility.Public ||
                request.Status is not (RequestStatus.New or RequestStatus.InProgress)) continue;
            items.Add(new(request.Id, request.Status, request.CreatedAt, request.Scope, request.BuildingNumber,
                request.Floor, request.Category, request.Location, request.Description, request.Entrance));
        }
        var next = page.LastEvaluatedKey?.GetValueOrDefault("requestCreatedAt")?.S;
        return new(items, next);
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
        var item = await GetAsync("USER#" + Number(userId), "REGISTRATION", cancellationToken);
        return item.TryGetValue("payload", out var payload)
            ? JsonSerializer.Deserialize<ResidentRegistration>(payload.S) : null;
    }

    public async Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken cancellationToken)
    {
        var item = await GetAsync("USER#" + Number(userId), "MEMBERSHIP", cancellationToken);
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
            Limit = 10
        }, cancellationToken);
        var requests = (response.Items ?? []).Select(item =>
            JsonSerializer.Deserialize<MaintenanceRequest>(item["payload"].S)
                ?? throw new InvalidOperationException("Invalid request data.")).ToList();
        return new RequestPage(requests,
            response.LastEvaluatedKey is { Count: > 0 } && requests.Count > 0 ? requests[^1].Id : null);
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

    public async Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome,
        CancellationToken cancellationToken)
    {
        var reply = new StoredReply(command.UpdateId, command.UserId, command.ChatId, outcome.Reply, false, outcome.Keyboard, outcome.Profile?.Value.PersonalDataDeleted == true || outcome.Registration?.AutomaticallyApproved == true);
        var replyItem = Key("UPDATE#" + Number(command.UpdateId), "REPLY");
        replyItem["payload"] = new AttributeValue(JsonSerializer.Serialize(reply));
        replyItem["delivered"] = new AttributeValue { BOOL = false };
        replyItem["expiresAt"] = new AttributeValue { N = Number(clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds()) };
        var writes = new List<TransactWriteItem> { Put(replyItem) };
        if (outcome.NewRequest is { } request)
        {
            if (!ComplexLayout.IsValid(request.Scope, request.BuildingNumber, request.Floor, request.Entrance))
                throw new InvalidOperationException("Request scope is required.");
            var item = Key("USER#" + Number(request.OwnerId), RequestKey(request.Id));
            item["payload"] = new AttributeValue(JsonSerializer.Serialize(request));
            item["requestBuildingId"] = new AttributeValue(request.BuildingId);
            item["requestCreatedAt"] = new AttributeValue(request.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#" + Number(request.Id));
            item["requestStatus"] = new AttributeValue(request.Status.ToString());
            item["requestCategory"] = new AttributeValue(request.Category);
            item["searchText"] = new AttributeValue((request.Location + " " + request.Description).ToLowerInvariant());
            item["requestTarget"] = new AttributeValue(ComplexLayout.Target(request.Scope, request.BuildingNumber));
            if (request.Floor is { } floor) item["requestFloor"] = new AttributeValue { N = Number(floor) };
            item["version"] = new AttributeValue { N = "1" };
            writes.Add(Put(item));
            var history = Key("REQUEST#" + Number(request.Id), "EVENT#CREATED");
            history["actorId"] = new AttributeValue { N = Number(request.OwnerId) };
            history["createdAt"] = new AttributeValue(request.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            history["status"] = new AttributeValue("New");
            if (request.Entrance is { } entrance) history["entrance"] = new AttributeValue { N = Number(entrance) };
            history["target"] = new AttributeValue(ComplexLayout.Target(request.Scope, request.BuildingNumber));
            if (request.Floor is { } historyFloor) history["floor"] = new AttributeValue { N = Number(historyFloor) };
            writes.Add(Put(history));
            writes.Add(NotificationWrite(AdministratorNotifications.NewRequest(request)));
        }

        if (outcome.Deletion is { } deletion)
        {
            if (deletion.OwnerId != command.UserId) throw new InvalidOperationException("Deletion owner mismatch.");
            writes.Add(new()
            {
                ConditionCheck = new()
                {
                    TableName = tableName,
                    Key = Key("USER#" + Number(command.UserId), "MEMBERSHIP"),
                    ConditionExpression = "buildingId = :complex AND #active = :active",
                    ExpressionAttributeNames = new() { ["#active"] = "active" },
                    ExpressionAttributeValues = new() { [":complex"] = new(deletion.ComplexId), [":active"] = new() { BOOL = true } }
                }
            });
            writes.Add(new()
            {
                Delete = new()
                {
                    TableName = tableName,
                    Key = Key("USER#" + Number(command.UserId), RequestKey(deletion.Id)),
                    ConditionExpression = "#version = :expected AND requestStatus = :new AND requestBuildingId = :complex",
                    ExpressionAttributeNames = new() { ["#version"] = "version" },
                    ExpressionAttributeValues = new()
                    {
                        [":expected"] = new() { N = Number(deletion.ExpectedVersion) },
                        [":new"] = new("New"),
                        [":complex"] = new(deletion.ComplexId)
                    }
                }
            });
            var history = Key("REQUEST#" + Number(deletion.Id), "EVENT#DELETED");
            history["actorId"] = new() { N = Number(command.UserId) };
            history["createdAt"] = new(clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            history["action"] = new("Deleted");
            history["previousStatus"] = new("New");
            writes.Add(Put(history));
        }

        if (outcome.Lookup is { } lookup)
        {
            if (lookup.Value.UserId != command.UserId) throw new InvalidOperationException("Lookup owner mismatch.");
            var item = Key("USER#" + Number(command.UserId), "REQUEST_LOOKUP");
            item["payload"] = new AttributeValue(JsonSerializer.Serialize(lookup.Value));
            item["version"] = new AttributeValue { N = Number(lookup.Value.Version) };
            var write = Put(item);
            if (lookup.ExpectedVersion is { } expected)
            {
                write.Put.ConditionExpression = "#version = :expected";
                write.Put.ExpressionAttributeNames = new() { ["#version"] = "version" };
                write.Put.ExpressionAttributeValues = new() { [":expected"] = new() { N = Number(expected) } };
            }
            writes.Add(write);
        }

        if (outcome.Draft is { } draft)
        {
            if (draft.Value.UserId != command.UserId) throw new InvalidOperationException("Draft owner mismatch.");
            var item = Key("USER#" + Number(command.UserId), "REQUEST_DRAFT");
            item["payload"] = new AttributeValue(JsonSerializer.Serialize(draft.Value));
            item["version"] = new AttributeValue { N = Number(draft.Value.Version) };
            var write = Put(item);
            if (draft.ExpectedVersion is { } expected)
            {
                write.Put.ConditionExpression = "#version = :expected";
                write.Put.ExpressionAttributeNames = new() { ["#version"] = "version" };
                write.Put.ExpressionAttributeValues = new() { [":expected"] = new() { N = Number(expected) } };
            }
            writes.Add(write);
        }

        if (outcome.Registration is { } change)
        {
            var registration = change.Value;
            if (registration.UserId != command.UserId)
                throw new InvalidOperationException("Registration owner mismatch.");
            var item = RegistrationItem("USER#" + Number(registration.UserId), "REGISTRATION", registration);
            var write = Put(item);
            if (change.ExpectedVersion is { } version)
            {
                write.Put.ConditionExpression = "#version = :expected";
                write.Put.ExpressionAttributeNames = new Dictionary<string, string> { ["#version"] = "version" };
                write.Put.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":expected"] = new AttributeValue { N = version.ToString(CultureInfo.InvariantCulture) }
                };
            }
            writes.Add(write);
            if (change.AutomaticallyApproved)
                AddAutomaticApprovalWrites(writes, registration, change.ExpectedVersion);
            else if (registration.Stage == RegistrationStage.Pending)
            {
                writes.Add(Put(RegistrationItem("REGISTRATION#" + registration.Id, "META", registration)));
                writes.Add(Put(RegistrationItem("BUILDING#" + registration.BuildingId,
                    "REGISTRATION#" + registration.Id, registration)));
                writes.Add(NotificationWrite(AdministratorNotifications.NewResident(registration)));
            }
        }

        AddProfileWrites(writes, command, outcome);
        try
        {
            await client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, cancellationToken);
            return reply;
        }
        catch (TransactionCanceledException)
        {
            // A concurrent invocation may already have committed this update.
            var existing = await GetReplyAsync(command.UpdateId, cancellationToken);
            if (existing is not null)
                return existing;
            throw;
        }
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
