using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<ResidentRegistration?> GetRegistrationAsync(string reference, CancellationToken ct)
    {
        var item = await GetResidentRecordAsync("REGISTRATION#" + reference, "META", ct);
        if (item is null) return null;
        var registration = Payload<ResidentRegistration>(item);
        return registration.Id == reference && registration.BuildingId == building ? registration : null;
    }

    public async Task<RegistrationApproval?> GetRegistrationApprovalAsync(string reference, CancellationToken ct)
    {
        var item = await GetResidentRecordAsync("REGISTRATION#" + reference, "EVENT#DECIDED", ct);
        if (item?.GetValueOrDefault("decision")?.S != "Approved" || item.GetValueOrDefault("actor")?.S is not { Length: > 0 } actor) return null;
        var timestamp = DateTimeOffset.TryParse(item.GetValueOrDefault("createdAt")?.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : (DateTimeOffset?)null;
        return new RegistrationApproval(actor, timestamp, actor == "system");
    }

    private async Task<Dictionary<string, AttributeValue>?> GetResidentRecordAsync(string pk, string sk, CancellationToken ct)
    {
        if (residentsTable is null) return await Get(dataTable, Key(pk, sk), ct);
        var record = ResidentRecordKey.Create(building, pk, sk);
        var item = await Get(residentsTable, new() { ["pk"] = S(record.Key) }, ct);
        return item?.GetValueOrDefault("recordData")?.M;
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
        var snapshot = await GetResidentRecordAsync("REGISTRATION#" + reference, "META", ct) ?? throw new ApiError(404, "Реєстрацію не знайдено.");
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
        var writes = new List<TransactWriteItem> { ActorCheck(actor), Update(Key("USER#" + current.UserId.ToString(CultureInfo.InvariantCulture), "REGISTRATION")),
            Update(Key("REGISTRATION#" + reference, "META")), Update(Key("BUILDING#" + building, listKey)), Put(dataTable, audit) };
        if (residentsTable is not null)
        {
            var registrationMeta = new Dictionary<string, AttributeValue>(snapshot) { ["pk"] = S("REGISTRATION#" + reference), ["sk"] = S("META") };
            registrationMeta["payload"] = S(JsonSerializer.Serialize(next));
            registrationMeta["version"] = N(next.Version);
            writes.Add(ResidentMirrorWrite(next, current.Version));
            writes.Add(RelatedResidentPut(registrationMeta, building, current.Version));
            var userKey = Key("USER#" + current.UserId.ToString(CultureInfo.InvariantCulture), "REGISTRATION");
            var directoryKey = Key("BUILDING#" + building, listKey);
            var originalUser = await GetResidentRecordAsync(userKey["pk"].S, userKey["sk"].S, ct) ?? throw new ApiError(409, "Профіль змінено. Оновіть список.");
            var originalDirectory = await GetResidentRecordAsync(directoryKey["pk"].S, directoryKey["sk"].S, ct) ?? throw new ApiError(409, "Профіль змінено. Оновіть список.");
            var userProfile = new Dictionary<string, AttributeValue>(originalUser)
            { ["pk"] = userKey["pk"], ["sk"] = userKey["sk"], ["payload"] = registrationMeta["payload"], ["version"] = registrationMeta["version"] };
            var directoryEntry = new Dictionary<string, AttributeValue>(originalDirectory)
            { ["pk"] = directoryKey["pk"], ["sk"] = directoryKey["sk"], ["payload"] = registrationMeta["payload"], ["version"] = registrationMeta["version"] };
            writes.Add(RelatedResidentPut(userProfile, building, current.Version));
            writes.Add(RelatedResidentPut(directoryEntry, building, current.Version));
            writes.Add(RelatedResidentPut(audit, building, immutable: true));
        }
        await Transact(writes, ct);
    }

    public async Task<Page<ResidentTypeChange>> RegistrationHistoryAsync(string reference, string? cursor, CancellationToken ct)
    {
        var pk = "REGISTRATION#" + reference;
        if (residentsTable is not null)
        {
            var group = ResidentRecordKey.Create(building, pk, "").Group;
            var startKey = Decode(cursor, "recordGroup", group);
            if (startKey is not null && (startKey.Count != 3 ||
                startKey.GetValueOrDefault("recordOrder")?.S?.StartsWith("EVENT#TYPE#", StringComparison.Ordinal) != true ||
                startKey.GetValueOrDefault("pk")?.S != ResidentRecordKey.Create(building, pk, startKey["recordOrder"].S).Key))
                throw new ApiError(400, "Некоректна сторінка.");
            var response = await db.QueryAsync(new QueryRequest
            {
                TableName = residentsTable,
                IndexName = "ResidentRecordsByGroup",
                KeyConditionExpression = "recordGroup = :group AND begins_with(recordOrder, :prefix)",
                ExpressionAttributeValues = new() { [":group"] = S(group), [":prefix"] = S("EVENT#TYPE#") },
                ExclusiveStartKey = startKey,
                ScanIndexForward = false,
                Limit = 25
            }, ct);
            return new Page<ResidentTypeChange>((response.Items ?? []).Select(item => Payload<ResidentTypeChange>(item["recordData"].M)).ToList(), Encode(response.LastEvaluatedKey));
        }
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
