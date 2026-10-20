using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Notifications;

public sealed class DynamoNotificationRepository(IAmazonDynamoDB db, string table, string complexId) : INotificationRepository
{
    private static AttributeValue S(string value) => new(value);
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private Dictionary<string, AttributeValue> Key(string sk) => new()
    {
        ["pk"] = S(RequestNotification.Partition(complexId)),
        ["sk"] = S(sk)
    };
    public async Task<IReadOnlyList<NotificationWorkItem>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var control = await db.GetItemAsync(new GetItemRequest { TableName = table, Key = Key("CONTROL"), ConsistentRead = true }, ct);
        if (control.Item?.TryGetValue("pauseUntil", out var pause) == true &&
            long.Parse(pause.N, CultureInfo.InvariantCulture) > now.ToUnixTimeSeconds()) return [];
        var result = await db.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            Limit = 25,
            KeyConditionExpression = "pk = :pk AND sk BETWEEN :start AND :end",
            ExpressionAttributeValues = new()
            {
                [":pk"] = S(RequestNotification.Partition(complexId)),
                [":start"] = S("DUE#"),
                [":end"] = S(RequestNotification.DueKey(now, "~"))
            }
        }, ct);
        return (result.Items ?? []).Select(item => new NotificationWorkItem(item["sk"].S,
            JsonSerializer.Deserialize<RequestNotification>(item["payload"].S) ?? throw new InvalidOperationException("Invalid notification."),
            int.Parse(item["attempts"].N, CultureInfo.InvariantCulture))).ToList();
    }
    public async Task<bool> ClaimAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct)
    {
        if (item.Notification.ComplexId != complexId || item.Notification.OwnerId <= 0)
            throw new InvalidOperationException("Notification identity mismatch.");
        try
        {
            await db.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key(item.SortKey),
                UpdateExpression = "SET leaseToken = :lease, leaseExpiresAt = :until",
                ConditionExpression = "attribute_exists(pk) AND (attribute_not_exists(leaseExpiresAt) OR leaseExpiresAt <= :now)",
                ExpressionAttributeValues = new()
                {
                    [":lease"] = S(lease),
                    [":until"] = N(now.AddSeconds(90).ToUnixTimeSeconds()),
                    [":now"] = N(now.ToUnixTimeSeconds())
                }
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }
    private TransactWriteItem Remove(NotificationWorkItem item, string lease) => new()
    {
        Delete = new()
        {
            TableName = table,
            Key = Key(item.SortKey),
            ConditionExpression = "leaseToken = :lease",
            ExpressionAttributeValues = new() { [":lease"] = S(lease) }
        }
    };
    public async Task CompleteAsync(NotificationWorkItem item, string lease, string disposition, DateTimeOffset now, CancellationToken ct)
    {
        var receipt = Key("RESULT#" + item.Notification.Id);
        receipt["disposition"] = S(disposition);
        receipt["completedAt"] = S(now.ToString("O", CultureInfo.InvariantCulture));
        receipt["expiresAt"] = N(now.AddDays(30).ToUnixTimeSeconds());
        await db.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [Remove(item, lease),
            new() { Put = new() { TableName = table, Item = receipt, ConditionExpression = "attribute_not_exists(pk)" } }]
        }, ct);
    }
    public async Task PauseAsync(DateTimeOffset until, CancellationToken ct)
    {
        try
        {
            await db.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Key("CONTROL"),
                UpdateExpression = "SET pauseUntil = :until",
                ConditionExpression = "attribute_not_exists(pauseUntil) OR pauseUntil < :until",
                ExpressionAttributeValues = new() { [":until"] = N(until.ToUnixTimeSeconds()) }
            }, ct);
        }
        catch (ConditionalCheckFailedException) { }
    }
    public async Task RetryAsync(NotificationWorkItem item, string lease, DateTimeOffset dueAt, CancellationToken ct)
    {
        var next = Key(RequestNotification.DueKey(dueAt, item.Notification.Id));
        next["payload"] = S(JsonSerializer.Serialize(item.Notification));
        next["attempts"] = N(item.Attempts + 1);
        await db.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [Remove(item, lease),
            new() { Put = new() { TableName = table, Item = next, ConditionExpression = "attribute_not_exists(pk)" } }]
        }, ct);
    }
}
