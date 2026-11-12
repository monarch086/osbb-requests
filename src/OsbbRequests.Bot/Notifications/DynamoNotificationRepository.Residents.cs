using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Bot.Notifications;

public sealed partial class DynamoNotificationRepository
{
    private async Task ExpandResidentsAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct)
    {
        var notification = item.Notification;
        if (!notification.IsBroadcast || notification.ComplexId != complexId || notification.AdministratorEvent is not null)
            throw new InvalidOperationException("Invalid resident broadcast.");
        var partition = "BUILDING#" + complexId;
        var legacy = residentsTable is null || item.AudienceCursor?.ContainsKey("sk") == true;
        QueryResponse page;
        if (legacy)
        {
            if (item.AudienceCursor is { } cursor && (cursor.Count != 2 || cursor.GetValueOrDefault("pk") != partition ||
                cursor.GetValueOrDefault("sk")?.StartsWith("RESIDENT#", StringComparison.Ordinal) != true))
                throw new InvalidOperationException("Invalid resident audience cursor.");
            page = await db.QueryAsync(new QueryRequest
            {
                TableName = table,
                ConsistentRead = true,
                Limit = 25,
                KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
                ExpressionAttributeValues = new() { [":pk"] = S(partition), [":prefix"] = S("RESIDENT#") },
                ExclusiveStartKey = item.AudienceCursor?.ToDictionary(pair => pair.Key, pair => S(pair.Value))
            }, ct);
        }
        else
        {
            var group = ResidentRecordKey.Create(complexId, partition, "").Group;
            if (item.AudienceCursor is { } cursor && (cursor.Count != 3 || cursor.GetValueOrDefault("recordGroup") != group ||
                !ValidResidentOrder(cursor.GetValueOrDefault("recordOrder"), out _) ||
                cursor.GetValueOrDefault("pk") != ResidentRecordKey.Create(complexId, partition, cursor["recordOrder"]).Key))
                throw new InvalidOperationException("Invalid resident audience cursor.");
            page = await db.QueryAsync(new QueryRequest
            {
                TableName = residentsTable,
                IndexName = "ResidentRecordsByGroup",
                Limit = 25,
                KeyConditionExpression = "recordGroup = :group AND begins_with(recordOrder, :prefix)",
                ExpressionAttributeValues = new() { [":group"] = S(group), [":prefix"] = S("RESIDENT#") },
                ProjectionExpression = "pk, recordGroup, recordOrder",
                ExclusiveStartKey = item.AudienceCursor?.ToDictionary(pair => pair.Key, pair => S(pair.Value))
            }, ct);
        }
        var writes = new List<TransactWriteItem>();
        foreach (var candidate in page.Items ?? [])
        {
            var row = candidate;
            long? candidateId = null;
            if (!legacy)
            {
                var order = candidate.GetValueOrDefault("recordOrder")?.S;
                if (!ValidResidentOrder(order, out var id) || candidate.GetValueOrDefault("recordGroup")?.S != ResidentRecordKey.Create(complexId, partition, "").Group ||
                    candidate.GetValueOrDefault("pk")?.S != ResidentRecordKey.Create(complexId, partition, order!).Key)
                    throw new InvalidOperationException("Invalid resident directory entry.");
                candidateId = id;
                var current = await db.GetItemAsync(new GetItemRequest
                {
                    TableName = residentsTable,
                    Key = new() { ["pk"] = candidate["pk"] },
                    ConsistentRead = true
                }, ct);
                row = current.Item?.GetValueOrDefault("recordData")?.M ?? [];
                if (row.Count == 0) continue;
            }
            var resident = JsonSerializer.Deserialize<ResidentRegistration>(row["payload"].S)
                ?? throw new InvalidOperationException("Invalid resident directory entry.");
            if (resident.BuildingId != complexId || resident.Stage != RegistrationStage.Approved || resident.PersonalDataDeleted || resident.UserId <= 0 ||
                candidateId is not null && resident.UserId != candidateId ||
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

    private static bool ValidResidentOrder(string? order, out long userId)
    {
        userId = 0;
        return order is { Length: 29 } && order.StartsWith("RESIDENT#", StringComparison.Ordinal) &&
            long.TryParse(order.AsSpan(9), NumberStyles.None, CultureInfo.InvariantCulture, out userId) && userId > 0 &&
            order == "RESIDENT#" + userId.ToString("D20", CultureInfo.InvariantCulture);
    }
}
