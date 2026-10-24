using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<Page<BroadcastMessage>> ListMessagesAsync(string? cursor, CancellationToken ct)
    {
        var partition = "MESSAGES#" + building;
        var start = Decode(cursor, "pk", partition);
        if (start is not null && (start.Count != 2 || start.GetValueOrDefault("sk")?.S?.StartsWith("MESSAGE#", StringComparison.Ordinal) != true))
            throw new ApiError(400, "Некоректна сторінка.");
        var page = await db.QueryAsync(new QueryRequest
        {
            TableName = dataTable,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = new() { [":pk"] = S(partition), [":prefix"] = S("MESSAGE#") },
            ExclusiveStartKey = start,
            ScanIndexForward = false,
            Limit = 25
        }, ct);
        return new((page.Items ?? []).Select(Payload<BroadcastMessage>).ToList(), Encode(page.LastEvaluatedKey));
    }

    public async Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct)
    {
        if (!actor.IsActive || !actor.IsSuperAdmin) throw new ApiError(403, "Недостатньо прав для розсилки.");
        if (!Guid.TryParseExact(change.Id, "D", out var id) || string.IsNullOrWhiteSpace(change.Text) || change.Text.Length > 4096)
            throw new ApiError(400, "Вкажіть текст повідомлення від 1 до 4096 символів і коректний ідентифікатор.");
        var messageId = id.ToString("D");
        var key = Key("MESSAGES#" + building, "ID#" + messageId);
        async Task<BroadcastMessage?> Existing()
        {
            var item = await Get(dataTable, key, ct);
            if (item is null) return null;
            var previous = Payload<BroadcastMessage>(item);
            if (previous.CreatedById != actor.UserId || previous.Text != change.Text.Trim())
                throw new ApiError(409, "Цей ідентифікатор вже використаний для іншого повідомлення.");
            return previous;
        }
        if (await Existing() is { } existing) return existing;
        var now = DateTimeOffset.UtcNow;
        var message = new BroadcastMessage(messageId, change.Text.Trim(), actor.Email, actor.UserId, now);
        var history = Key("MESSAGES#" + building, "MESSAGE#" + now.ToString("O") + "#" + messageId);
        history["payload"] = S(JsonSerializer.Serialize(message));
        var identity = new Dictionary<string, AttributeValue>(key) { ["payload"] = history["payload"] };
        var notification = new ResidentNotification("message-" + messageId, building, 0, null, null, message.Text, now,
            ResidentBroadcastId: messageId);
        try
        {
            // The immutable history is also the audit: identity and timestamp are assigned by the server.
            await Transact([ActorCheck(actor, super: true), Put(dataTable, identity), Put(dataTable, history), NotificationWrite(notification)], ct);
        }
        catch (ApiError error) when (error.Status == 409)
        {
            if (await Existing() is { } concurrent) return concurrent;
            throw;
        }
        return message;
    }
}
