using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Notifications;

public sealed partial class DynamoNotificationRepository
{
    private async Task<(bool Active, AdministratorNotificationSettings Settings)> AdministratorAsync(string id, CancellationToken ct)
    {
        if (administratorsTable is null) throw new InvalidOperationException("Administrator notification configuration missing.");
        var result = await db.GetItemAsync(new GetItemRequest
        {
            TableName = administratorsTable,
            Key = new() { ["UserId"] = S(id) },
            ConsistentRead = true
        }, ct);
        var data = result.Item ?? [];
        return (data.GetValueOrDefault("IsActive")?.BOOL == true,
            new(data.GetValueOrDefault("TelegramChatId")?.S, data.GetValueOrDefault("NotifyNewResidents")?.BOOL == true,
                data.GetValueOrDefault("NotifyNewRequests")?.BOOL == true, data.GetValueOrDefault("NotifyNewAdministrators")?.BOOL == true));
    }
    public async Task<bool> CanDeliverAsync(NotificationWorkItem item, CancellationToken ct)
    {
        var notification = item.Notification;
        if (notification.ComplexId != complexId || notification.IsBroadcast) return false;
        if (notification.AdministratorId is null) return notification.OwnerId > 0 && notification.AdministratorEvent is null;
        var (active, settings) = await AdministratorAsync(notification.AdministratorId, ct);
        return active && notification.AdministratorEvent is { } kind && settings.Includes(kind) && settings.ChatId == notification.OwnerId;
    }
    public async Task ExpandAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct)
    {
        var notification = item.Notification;
        if (!notification.IsBroadcast || notification.ComplexId != complexId || administratorsTable is null)
            throw new InvalidOperationException("Invalid administrator broadcast.");
        var page = await db.QueryAsync(new QueryRequest
        {
            TableName = administratorsTable,
            IndexName = "DirectoryIndex",
            Limit = 25,
            KeyConditionExpression = "Directory = :directory",
            ExpressionAttributeValues = new() { [":directory"] = S("ADMINS") },
            ExclusiveStartKey = item.AudienceCursor?.ToDictionary(pair => pair.Key, pair => S(pair.Value))
        }, ct);
        var writes = new List<TransactWriteItem>();
        foreach (var candidate in page.Items ?? [])
        {
            var id = candidate["UserId"].S;
            var (active, settings) = await AdministratorAsync(id, ct);
            if (!active || !settings.Includes(notification.AdministratorEvent!.Value) || settings.ChatId is not { } chat) continue;
            var recipient = notification with { Id = notification.Id + "-admin-" + id, OwnerId = chat, AdministratorId = id };
            var job = Key(ResidentNotification.DueKey(now, recipient.Id));
            job["payload"] = S(JsonSerializer.Serialize(recipient));
            job["attempts"] = N(0);
            writes.Add(new() { Put = new() { TableName = table, Item = job, ConditionExpression = "attribute_not_exists(pk)" } });
        }
        if (page.LastEvaluatedKey is { Count: > 0 })
        {
            writes.Add(new()
            {
                Update = new()
                {
                    TableName = table,
                    Key = Key(item.SortKey),
                    UpdateExpression = "SET audienceCursor = :cursor REMOVE leaseToken, leaseExpiresAt",
                    ConditionExpression = "leaseToken = :lease",
                    ExpressionAttributeValues = new()
                    {
                        [":cursor"] = S(JsonSerializer.Serialize(page.LastEvaluatedKey.ToDictionary(pair => pair.Key, pair => pair.Value.S))),
                        [":lease"] = S(lease)
                    }
                }
            });
        }
        else writes.AddRange(CompletionWrites(item, lease, "Expanded", now));
        await db.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
    }
}
