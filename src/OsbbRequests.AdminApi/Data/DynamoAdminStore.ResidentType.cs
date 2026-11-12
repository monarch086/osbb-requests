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

    public async Task<RegistrationApproval?> GetRegistrationApprovalAsync(string reference, CancellationToken ct)
    {
        var item = await Get(dataTable, Key("REGISTRATION#" + reference, "EVENT#DECIDED"), ct);
        if (item?.GetValueOrDefault("decision")?.S != "Approved" || item.GetValueOrDefault("actor")?.S is not { Length: > 0 } actor) return null;
        var timestamp = DateTimeOffset.TryParse(item.GetValueOrDefault("createdAt")?.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : (DateTimeOffset?)null;
        return new RegistrationApproval(actor, timestamp, actor == "system");
    }

    public Task ChangeResidentTypeAsync(AdminUser actor, string reference, ResidentTypeChangeRequest change, CancellationToken ct) =>
        ApplyResidentChangeAsync(actor, reference, change.ResidentType, change.Version, null, null, true, ct);

    public Task UpdateResidentDataAsync(AdminUser actor, string reference, ResidentDataChangeRequest change, CancellationToken ct)
    {
        if (!ValidName(change.FirstName) || !ValidName(change.LastName)) throw new ApiError(400, "Вкажіть коректні ім’я та прізвище.");
        return ApplyResidentChangeAsync(actor, reference, change.ResidentType, change.Version, change.FirstName.Trim(), change.LastName.Trim(), false, ct);
    }

    private static bool ValidName(string? value)
    {
        var name = value?.Trim() ?? "";
        return name.Length is >= 1 and <= 80 && name.Any(char.IsLetter) &&
            name.All(c => char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark ||
                c is ' ' or '-' or '\'' or '’' or 'ʼ');
    }

    private async Task ApplyResidentChangeAsync(AdminUser actor, string reference, string residentType, int version, string? firstName, string? lastName, bool pendingOnly, CancellationToken ct)
    {
        if (!actor.IsActive || !actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
        if (!Enum.GetNames<ResidentType>().Contains(residentType, StringComparer.Ordinal)) throw new ApiError(400, "Оберіть коректний тип мешканця.");
        var snapshot = await Get(dataTable, Key("REGISTRATION#" + reference, "META"), ct) ?? throw new ApiError(404, "Реєстрацію не знайдено.");
        var current = Payload<ResidentRegistration>(snapshot);
        if (current.Id != reference || current.BuildingId != building) throw new ApiError(404, "Реєстрацію не знайдено.");
        if ((current.Stage != RegistrationStage.Pending && (pendingOnly || current.Stage != RegistrationStage.Approved || current.PersonalDataDeleted)) || current.Version != version)
            throw new ApiError(409, "Дані мешканця змінено. Оновіть список.");
        var type = Enum.Parse<ResidentType>(residentType);
        firstName ??= current.FirstName;
        lastName ??= current.LastName;
        if (current.ResidentType == type && current.FirstName == firstName && current.LastName == lastName) throw new ApiError(400, "Дані не змінено.");
        var next = current with { FirstName = firstName, LastName = lastName, ResidentType = type, Version = current.Version + 1, TypeChangeCount = checked(current.TypeChangeCount + 1) };
        var now = DateTimeOffset.UtcNow;
        var audit = Key("REGISTRATION#" + reference, "EVENT#TYPE#" + now.ToString("O", CultureInfo.InvariantCulture) + "#" + Guid.NewGuid().ToString("N"));
        audit["payload"] = S(JsonSerializer.Serialize(new ResidentTypeChange(current.ResidentType.ToString(), type.ToString(), actor.Email, now, current.FirstName != firstName, current.LastName != lastName)));
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
                    [":expected"] = N(version)
                }
            }
        };
        var listKey = current.Stage == RegistrationStage.Pending ? "REGISTRATION#" + reference : "RESIDENT#" + current.UserId.ToString("D20", CultureInfo.InvariantCulture);
        await Transact([ActorCheck(actor), Update(Key("USER#" + current.UserId.ToString(CultureInfo.InvariantCulture), "REGISTRATION")),
            Update(Key("REGISTRATION#" + reference, "META")), Update(Key("BUILDING#" + building, listKey)), Put(dataTable, audit)], ct);
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
