using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Persistence;

public sealed class DynamoCommandRepository(IAmazonDynamoDB client, string tableName,
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
        var reply = new StoredReply(command.UpdateId, command.UserId, command.ChatId, outcome.Reply, false);
        var replyItem = Key("UPDATE#" + Number(command.UpdateId), "REPLY");
        replyItem["payload"] = new AttributeValue(JsonSerializer.Serialize(reply));
        replyItem["delivered"] = new AttributeValue { BOOL = false };
        replyItem["expiresAt"] = new AttributeValue { N = Number(clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds()) };
        var writes = new List<TransactWriteItem> { Put(replyItem) };
        if (outcome.NewRequest is { } request)
        {
            var item = Key("USER#" + Number(request.OwnerId), RequestKey(request.Id));
            item["payload"] = new AttributeValue(JsonSerializer.Serialize(request));
            writes.Add(Put(item));
            var history = Key("REQUEST#" + Number(request.Id), "EVENT#CREATED");
            history["actorId"] = new AttributeValue { N = Number(request.OwnerId) };
            history["createdAt"] = new AttributeValue(request.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            history["status"] = new AttributeValue("New");
            writes.Add(Put(history));
        }

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

    private TransactWriteItem Put(Dictionary<string, AttributeValue> item) => new()
    {
        Put = new Put { TableName = tableName, Item = item, ConditionExpression = "attribute_not_exists(pk)" }
    };
}
