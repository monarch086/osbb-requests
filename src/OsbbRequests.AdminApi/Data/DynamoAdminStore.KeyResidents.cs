using OsbbRequests.Services;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Models;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Shared.Residents;
using OsbbRequests.Domain;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<ResidentKeyPage> ListResidentsWithKeysAsync(int page, CancellationToken ct)
    {
        if (page < 1 || page > 1000000) throw new ApiError(400, "Некоректна сторінка.");
        const int pageSize = 25;
        var keys = new List<(long RequestId, long OwnerId, int Index, string Phone)>();
        var filters = new RequestFilters([nameof(RequestStatus.Completed)], null, null, null, null, Section: "keys");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var batch = await ListRequestsAsync(filters, cursor, ct);
            foreach (var candidate in batch.Items)
            {
                var request = candidate.Request;
                if (request is not { Category: RequestCategories.KeyOrder, Status: RequestStatus.Completed, KeyItems: { } items }) continue;
                for (var index = 0; index < items.Count; index++)
                    keys.Add((request.Id, request.OwnerId, index, KeyDirectoryPhone(items[index].Phone)));
            }
            cursor = batch.Cursor;
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated request cursor.");
        } while (cursor is not null);
        var groups = keys.OrderByDescending(key => key.RequestId).ThenBy(key => key.OwnerId).ThenBy(key => key.Index)
            .GroupBy(key => key.Phone, StringComparer.Ordinal).ToArray();
        if (groups.Length == 0) return new([], 1, pageSize, 0);
        var actual = Math.Min(page, (groups.Length - 1) / pageSize + 1);
        var selected = groups.Skip((actual - 1) * pageSize).Take(pageSize);
        var requests = new Dictionary<(long OwnerId, long RequestId), ManagedRequest?>();
        var current = new List<(string Phone, IReadOnlyList<ResidentKey> Keys)>();
        foreach (var group in selected)
        {
            var currentKeys = new List<ResidentKey>();
            foreach (var key in group)
            {
                var identity = (key.OwnerId, key.RequestId);
                if (!requests.TryGetValue(identity, out var latest))
                    requests[identity] = latest = await GetRequestAsync(key.OwnerId, key.RequestId, ct);
                if (latest?.Request is not { Category: RequestCategories.KeyOrder, Status: RequestStatus.Completed, KeyItems: { } items } || key.Index >= items.Count) continue;
                var item = items[key.Index];
                if (KeyDirectoryPhone(item.Phone) != group.Key) continue;
                currentKeys.Add(new(item.Type, item.SerialNumber, key.RequestId, key.Index, key.OwnerId, latest.Version));
            }
            if (currentKeys.Count > 0) current.Add((group.Key, currentKeys));
        }
        var residents = await FindKeyResidentsAsync(current.Select(group => group.Phone), ct);
        var rows = current.Select((group, index) => new ResidentKeyEntry(group.Phone, residents[index], group.Keys)).ToArray();
        return new(rows, actual, pageSize, groups.Length);
    }

    private static string KeyDirectoryPhone(string phone) => ResidentPhoneIndex.TryNormalize(phone, out var normalized) ? normalized! : phone;

    public async Task UpdateResidentKeyAsync(AdminUser actor, long ownerId, long requestId, int index, ResidentKeyUpdate change, CancellationToken ct)
    {
        if (!actor.IsActive || !actor.EffectivePermissions.KeyOrders) throw new ApiError(403, "Недостатньо прав для редагування ключів.");
        if (change.Version is null || change.Version < 0 ||
            (change.Remove ? change.Type is not null || change.SerialNumber is not null : change.Type is not ("Магніт" or "Перепустка")) ||
            change.SerialNumber is { Length: > 25 }) throw new ApiError(400, "Оберіть тип ключа та вкажіть серійний номер до 25 символів за наявності.");
        var current = await GetRequestAsync(ownerId, requestId, ct) ?? throw new ApiError(404, "Заявку не знайдено.");
        if (current.Version != change.Version || current.Request is not { Category: RequestCategories.KeyOrder, Status: RequestStatus.Completed, KeyItems: { } items } || index < 0 || index >= items.Count)
            throw new ApiError(409, "Замовлення або ключ змінено. Оновіть список.");
        var previous = items[index];
        var updated = change.Remove ? null : previous with { Type = change.Type!, SerialNumber = string.IsNullOrWhiteSpace(change.SerialNumber) ? null : change.SerialNumber.Trim() };
        if (updated == previous) throw new ApiError(400, "Дані ключа не змінено.");

        var profiles = (await FindKeyProfilesAsync([previous.Phone], ct))[0].DistinctBy(profile => profile.UserId).ToArray();
        if (profiles.Length == 0) throw new ApiError(409, "Профіль мешканця для цього ключа не знайдено.");
        // The request update, author check and request audit use three of the 100 transaction actions.
        if (profiles.Length > 48) throw new ApiError(409, "Цей номер пов’язано із завеликою кількістю профілів.");
        var now = DateTimeOffset.UtcNow;
        var details = new ResidentKeyChange(requestId, new(previous.Type, previous.SerialNumber), updated is null ? null : new(updated.Type, updated.SerialNumber));
        var payload = S(JsonSerializer.Serialize(new ResidentTypeChange(null, null, actor.Email, now, KeyChange: details)));
        var writes = new List<TransactWriteItem>();
        foreach (var profile in profiles)
        {
            var profileKey = ResidentRecordKey.Create(building, "USER#" + profile.UserId.ToString(CultureInfo.InvariantCulture), "REGISTRATION");
            writes.Add(new()
            {
                ConditionCheck = new()
                {
                    TableName = residentsTable!,
                    Key = new() { ["pk"] = S(profileKey.Key) },
                    ConditionExpression = "#v = :version",
                    ExpressionAttributeNames = new() { ["#v"] = "version" },
                    ExpressionAttributeValues = new() { [":version"] = N(profile.Version) }
                }
            });
            var audit = Key("REGISTRATION#" + profile.Id, "EVENT#TYPE#" + now.ToString("O", CultureInfo.InvariantCulture) + "#" + Guid.NewGuid().ToString("N"));
            audit["payload"] = payload;
            writes.Add(RelatedResidentPut(audit, building, immutable: true));
        }

        var remaining = items.ToList();
        if (updated is null) remaining.RemoveAt(index);
        else remaining[index] = updated;
        var next = current.Request with { KeyItems = remaining, KeyQuantity = remaining.Count };
        await SaveRequestAsync(actor, current, next, ct, residentWrites: writes);
    }

    public Task<IReadOnlyList<IReadOnlyList<KeyResident>>> GetKeyResidentsAsync(MaintenanceRequest request, CancellationToken ct)
    {
        if (request.BuildingId != building || request.Category != RequestCategories.KeyOrder)
            throw new InvalidOperationException("Invalid key order.");
        return FindKeyResidentsAsync((request.KeyItems ?? []).Select(item => item.Phone), ct);
    }

    private async Task<IReadOnlyList<IReadOnlyList<KeyResident>>> FindKeyResidentsAsync(IEnumerable<string> phones, CancellationToken ct)
    {
        var groups = await FindKeyProfilesAsync(phones, ct);
        return groups.Select(group => (IReadOnlyList<KeyResident>)group.Select(profile => new KeyResident(
            string.Join(" ", new[] { profile.FirstName, profile.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
            profile.BuildingNumber, profile.ApartmentNumber)).Where(resident => resident.Name.Length > 0).ToArray()).ToArray();
    }

    private async Task<IReadOnlyList<IReadOnlyList<ResidentRegistration>>> FindKeyProfilesAsync(IEnumerable<string> phones, CancellationToken ct)
    {
        if (residentsTable is null) throw new InvalidOperationException("Invalid key order.");
        var cache = new Dictionary<string, IReadOnlyList<ResidentRegistration>>(StringComparer.Ordinal);
        var results = new List<IReadOnlyList<ResidentRegistration>>();
        foreach (var value in phones)
        {
            if (!ResidentPhoneIndex.TryNormalize(value, out var phone)) { results.Add([]); continue; }
            if (!cache.TryGetValue(phone!, out var names))
            {
                var matches = new List<ResidentRegistration>();
                var lookup = ResidentPhoneIndex.LookupKey(building, phone!);
                Dictionary<string, AttributeValue>? cursor = null;
                do
                {
                    var page = await db.QueryAsync(new QueryRequest
                    {
                        TableName = residentsTable,
                        IndexName = ResidentPhoneIndex.IndexName,
                        KeyConditionExpression = "phoneLookup = :phone",
                        ExpressionAttributeValues = new() { [":phone"] = S(lookup) },
                        ExclusiveStartKey = cursor
                    }, ct);
                    foreach (var candidate in page.Items ?? [])
                    {
                        if (!candidate.TryGetValue("pk", out var key) || string.IsNullOrEmpty(key.S)) continue;
                        var row = await Get(residentsTable, new() { ["pk"] = key }, ct);
                        if (row is null || row.GetValueOrDefault("complexId")?.S != building || row.GetValueOrDefault("phoneLookup")?.S != lookup ||
                            !long.TryParse(row.GetValueOrDefault("residentId")?.N, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                            key.S != ResidentPhoneIndex.ItemKey(building, id, phone!)) continue;
                        var source = await GetResidentRecordAsync("USER#" + id.ToString(CultureInfo.InvariantCulture), "REGISTRATION", ct);
                        if (source is null) continue;
                        var resident = Payload<ResidentRegistration>(source);
                        if (resident.UserId != id || resident.BuildingId != building || resident.Stage != RegistrationStage.Approved || resident.PersonalDataDeleted ||
                            !ResidentPhoneIndex.TryNormalize(resident.Phone, out var currentPhone) || currentPhone != phone) continue;
                        matches.Add(resident);
                    }
                    cursor = page.LastEvaluatedKey;
                } while (cursor is { Count: > 0 });
                cache[phone!] = names = matches;
            }
            results.Add(names);
        }
        return results;
    }
}
