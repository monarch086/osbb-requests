using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<ResidentRegistration?> GetRegistrationAsync(string reference, CancellationToken ct)
    {
        var item = await Get(dataTable, Key("REGISTRATION#" + reference, "META"), ct);
        if (item is null) return null;
        var registration = Payload<ResidentRegistration>(item);
        return registration.Id == reference && registration.BuildingId == building ? registration : null;
    }

    public async Task ChangeResidentTypeAsync(AdminUser actor, string reference, ResidentTypeChangeRequest change, CancellationToken ct)
    {
        if (!actor.IsActive || !actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
        if (!Enum.GetNames<ResidentType>().Contains(change.ResidentType, StringComparer.Ordinal)) throw new ApiError(400, "Оберіть коректний тип мешканця.");
        var snapshot = await Get(dataTable, Key("REGISTRATION#" + reference, "META"), ct) ?? throw new ApiError(404, "Реєстрацію не знайдено.");
        var current = Payload<ResidentRegistration>(snapshot);
        if (current.Id != reference || current.BuildingId != building) throw new ApiError(404, "Реєстрацію не знайдено.");
        if (current.Stage != RegistrationStage.Pending || current.Version != change.Version) throw new ApiError(409, "Реєстрацію змінено. Оновіть список.");
        var type = Enum.Parse<ResidentType>(change.ResidentType);
        if (current.ResidentType == type) throw new ApiError(400, "Оберіть інший тип мешканця.");
        var next = current with { ResidentType = type, Version = current.Version + 1, TypeChangeCount = checked(current.TypeChangeCount + 1) };
        var now = DateTimeOffset.UtcNow;
        var audit = Key("REGISTRATION#" + reference, "EVENT#TYPE#" + now.ToString("O", CultureInfo.InvariantCulture) + "#" + Guid.NewGuid().ToString("N"));
        audit["payload"] = S(JsonSerializer.Serialize(new ResidentTypeChange(current.ResidentType.ToString(), type.ToString(), actor.Email, now)));
        TransactWriteItem Update(Dictionary<string, AttributeValue> key) => new()
        {
            Update = new()
            {
                TableName = dataTable,
                Key = key,
                UpdateExpression = "SET #payload = :next, #v = :nextVersion",
                ConditionExpression = "#payload = :previous AND #v = :expected",
                ExpressionAttributeNames = new() { ["#payload"] = "payload", ["#v"] = "version" },
                ExpressionAttributeValues = new()
                {
                    [":next"] = S(JsonSerializer.Serialize(next)),
                    [":previous"] = snapshot["payload"],
                    [":nextVersion"] = N(next.Version),
                    [":expected"] = N(change.Version)
                }
            }
        };
        await Transact([ActorCheck(actor), Update(Key("USER#" + current.UserId.ToString(CultureInfo.InvariantCulture), "REGISTRATION")),
            Update(Key("REGISTRATION#" + reference, "META")), Update(Key("BUILDING#" + building, "REGISTRATION#" + reference)), Put(dataTable, audit)], ct);
    }

    public async Task<Page<ResidentTypeChange>> RegistrationHistoryAsync(string reference, string? cursor, CancellationToken ct)
    {
        var pk = "REGISTRATION#" + reference;
        var start = Decode(cursor, "pk", pk);
        if (start is not null && (start.Count != 2 || start.GetValueOrDefault("sk")?.S?.StartsWith("EVENT#TYPE#", StringComparison.Ordinal) != true))
            throw new ApiError(400, "Некоректна сторінка.");
        var result = await db.QueryAsync(new QueryRequest
        {
            TableName = dataTable,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk AND begins_with(sk, :prefix)",
            ExpressionAttributeValues = new() { [":pk"] = S(pk), [":prefix"] = S("EVENT#TYPE#") },
            ExclusiveStartKey = start,
            ScanIndexForward = false,
            Limit = 25
        }, ct);
        return new Page<ResidentTypeChange>((result.Items ?? []).Select(Payload<ResidentTypeChange>).ToList(), Encode(result.LastEvaluatedKey));
    }
}
