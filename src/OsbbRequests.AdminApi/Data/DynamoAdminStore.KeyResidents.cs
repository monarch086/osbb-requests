using System.Globalization;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Residents;
using OsbbRequests.Shared.Domain;

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
                currentKeys.Add(new(item.Type, item.SerialNumber, key.RequestId, key.Index));
            }
            if (currentKeys.Count > 0) current.Add((group.Key, currentKeys));
        }
        var residents = await FindKeyResidentsAsync(current.Select(group => group.Phone), ct);
        var rows = current.Select((group, index) => new ResidentKeyEntry(group.Phone, residents[index], group.Keys)).ToArray();
        return new(rows, actual, pageSize, groups.Length);
    }

    private static string KeyDirectoryPhone(string phone) => ResidentPhoneIndex.TryNormalize(phone, out var normalized) ? normalized! : phone;

    public Task<IReadOnlyList<IReadOnlyList<KeyResident>>> GetKeyResidentsAsync(MaintenanceRequest request, CancellationToken ct)
    {
        if (request.BuildingId != building || request.Category != RequestCategories.KeyOrder)
            throw new InvalidOperationException("Invalid key order.");
        return FindKeyResidentsAsync((request.KeyItems ?? []).Select(item => item.Phone), ct);
    }

    private async Task<IReadOnlyList<IReadOnlyList<KeyResident>>> FindKeyResidentsAsync(IEnumerable<string> phones, CancellationToken ct)
    {
        if (residentsTable is null) throw new InvalidOperationException("Invalid key order.");
        var cache = new Dictionary<string, IReadOnlyList<KeyResident>>(StringComparer.Ordinal);
        var results = new List<IReadOnlyList<KeyResident>>();
        foreach (var value in phones)
        {
            if (!ResidentPhoneIndex.TryNormalize(value, out var phone)) { results.Add([]); continue; }
            if (!cache.TryGetValue(phone!, out var names))
            {
                var matches = new List<KeyResident>();
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
                        var name = string.Join(" ", new[] { resident.FirstName, resident.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                        if (name.Length > 0) matches.Add(new(name, resident.BuildingNumber, resident.ApartmentNumber));
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
