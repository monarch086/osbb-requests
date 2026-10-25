using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Notifications;

public sealed partial class DynamoNotificationRepository
{
    private async Task ExpandResidentsAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct)
    {
        var notification = item.Notification;
        if (!notification.IsBroadcast || notification.ComplexId != complexId || notification.AdministratorEvent is not null)
            throw new InvalidOperationException("Invalid resident broadcast.");
        var partition = "BUILDING#" + complexId;
        if (item.AudienceCursor is { } cursor && (cursor.GetValueOrDefault("pk") != partition ||
            cursor.GetValueOrDefault("sk")?.StartsWith("RESIDENT#", StringComparison.Ordinal) != true))
            throw new InvalidOperationException("Invalid resident audience cursor.");
        var page = await db.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            Limit = 25,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = new() { [":pk"] = S(partition), [":prefix"] = S("RESIDENT#") },
            ExclusiveStartKey = item.AudienceCursor?.ToDictionary(pair => pair.Key, pair => S(pair.Value))
        }, ct);
        var writes = new List<TransactWriteItem>();
        foreach (var candidate in page.Items ?? [])
        {
            var resident = JsonSerializer.Deserialize<ResidentRegistration>(candidate["payload"].S)
                ?? throw new InvalidOperationException("Invalid resident directory entry.");
            if (resident.BuildingId != complexId || resident.Stage != RegistrationStage.Approved || resident.UserId <= 0 ||
                (resident.ApprovedAt ?? resident.UpdatedAt) > notification.CreatedAt) continue;
            var recipient = notification with
            {
                Id = notification.Id + "-resident-" + resident.UserId.ToString(CultureInfo.InvariantCulture),
                OwnerId = resident.UserId
            };
            var job = Key(ResidentNotification.DueKey(now, recipient.Id));
            job["payload"] = S(JsonSerializer.Serialize(recipient));
            job["attempts"] = N(0);
            writes.Add(new() { Put = new() { TableName = table, Item = job, ConditionExpression = "attribute_not_exists(pk)" } });
        }
        // Persist the next audience page and its recipient jobs atomically under the parent's lease.
        await SaveExpansionAsync(item, lease, now, writes, page.LastEvaluatedKey, ct);
    }
}
