using System.Globalization;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Shared.Models;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<ResidentKeyPage> ListResidentsWithKeysAsync(int page, CancellationToken ct)
    {
        if (page < 1 || page > 1000000) throw new ApiError(400, "Некоректна сторінка.");
        var keys = new List<(long RequestId, int Index, KeyOrderItem Item)>();
        var filters = new RequestFilters(null, null, null, null, null, Section: "keys");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var batch = await ListRequestsAsync(filters, cursor, ct);
            foreach (var candidate in batch.Items)
            {
                var latest = await GetRequestAsync(candidate.Request.OwnerId, candidate.Request.Id, ct);
                if (latest?.Request is not { Category: RequestCategories.KeyOrder, KeyItems: { } items } request) continue;
                for (var index = 0; index < items.Count; index++) keys.Add((request.Id, index, items[index]));
            }
            cursor = batch.Cursor;
            if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Repeated request cursor.");
        } while (cursor is not null);
        if (keys.Count == 0) return new([], 1, 25, 0);
        var names = await GetKeyResidentNamesAsync(new MaintenanceRequest(0, 0, building, RequestCategories.KeyOrder,
            "", "", DateTimeOffset.UnixEpoch, KeyItems: keys.Select(key => key.Item).ToArray()), ct);
        var rows = keys.SelectMany((key, index) => names[index].Select(name => new ResidentKeyEntry(name, key.Item.Phone,
            key.Item.SerialNumber, key.RequestId, key.Index))).OrderBy(row => row.Name, StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true))
            .ThenBy(row => row.Phone, StringComparer.Ordinal).ThenBy(row => row.RequestId).ThenBy(row => row.KeyIndex).ToList();
        var actual = Math.Min(page, Math.Max(1, (rows.Count + 24) / 25));
        return new(rows.Skip((actual - 1) * 25).Take(25).ToArray(), actual, 25, rows.Count);
    }

    public async Task<IReadOnlyList<IReadOnlyList<string>>> GetKeyResidentNamesAsync(MaintenanceRequest request, CancellationToken ct)
    {
        if (request.BuildingId != building || request.Category != RequestCategories.KeyOrder || residentsTable is null)
            throw new InvalidOperationException("Invalid key order.");
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var results = new List<IReadOnlyList<string>>();
        foreach (var item in request.KeyItems ?? [])
        {
            if (!ResidentPhoneIndex.TryNormalize(item.Phone, out var phone)) { results.Add([]); continue; }
            if (!cache.TryGetValue(phone!, out var names))
            {
                var matches = new List<string>();
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
                        if (name.Length > 0) matches.Add(name);
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
