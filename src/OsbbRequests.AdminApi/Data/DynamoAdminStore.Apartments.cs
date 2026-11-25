using OsbbRequests.Services;
using System.Globalization;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.AdminApi.Data;

public sealed partial class DynamoAdminStore
{
    public async Task<IReadOnlyList<OwnerApartment>> ListOwnerApartmentsAsync(ResidentRegistration profile, CancellationToken ct)
    {
        if (!OwnerApartments.CanManage(profile) || residentsTable is null) return [];
        var record = await GetOwnerApartmentRecordAsync(profile.UserId, ct);
        return record?.RegistrationId == profile.Id ? record.Items : [];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<OwnerApartment>>> ListApprovedApartmentsForExportAsync(
        IReadOnlyList<ResidentRegistration> residents, CancellationToken ct)
    {
        var approved = new Dictionary<string, IReadOnlyList<OwnerApartment>>(StringComparer.Ordinal);
        if (residentsTable is null) return approved;
        var owners = residents.Where(profile => profile.BuildingId == building && profile.UserId > 0 && OwnerApartments.CanManage(profile))
            .DistinctBy(profile => profile.UserId);
        foreach (var batch in owners.Chunk(100))
        {
            var keys = batch.Select(profile => new Dictionary<string, AttributeValue>
            {
                ["pk"] = S(OwnerApartments.Key(building, profile.UserId))
            }).ToList();
            var rows = await BatchReadResidentRecordsAsync(residentsTable, keys, ct);
            foreach (var owner in batch)
            {
                if (!rows.TryGetValue(OwnerApartments.Key(building, owner.UserId), out var row)) continue;
                var record = OwnerApartments.Read(row, building, owner.UserId);
                if (record?.RegistrationId != owner.Id) continue;
                approved[owner.Id] = record.Items.Where(apartment => apartment.Status == ApartmentReviewStatus.Approved).ToList();
            }
        }
        return approved;
    }

    public async Task<int> CountApartmentReviewsAsync(CancellationToken ct)
    {
        var count = 0;
        string? cursor = null;
        do
        {
            var page = await ListApartmentReviewsAsync(cursor, ct);
            count += page.Items.Sum(entry => entry.Apartments.Count);
            cursor = page.Cursor;
        } while (cursor is not null);
        return count;
    }

    public async Task<Page<ApartmentReviewEntry>> ListApartmentReviewsAsync(string? cursor, CancellationToken ct)
    {
        var table = residentsTable ?? throw new InvalidOperationException("Missing residents table.");
        var group = OwnerApartments.ReviewGroup(building);
        const string field = ResidentRecordKey.IndexGroupAttribute;
        var start = Decode(cursor, field, group);
        if (start is not null && (start.Count != 3 || !TryReviewOwner(start, out _)))
            throw new ApiError(400, "Некоректна сторінка.");
        var page = await db.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = ResidentRecordKey.CompactIndex,
            KeyConditionExpression = "recordIndexGroup = :group",
            ExpressionAttributeValues = new() { [":group"] = S(group) },
            ExclusiveStartKey = start,
            Limit = 10
        }, ct);
        var items = new List<ApartmentReviewEntry>();
        foreach (var key in page.Items ?? [])
        {
            if (!TryReviewOwner(key, out var owner)) throw new InvalidOperationException("Invalid apartment review key.");
            var record = await GetOwnerApartmentRecordAsync(owner, ct);
            if (record is null || !record.Items.Any(item => item.Status == ApartmentReviewStatus.Pending)) continue;
            var profileItem = await GetResidentRecordAsync(FormattableString.Invariant($"USER#{owner}"), "REGISTRATION", ct);
            var profile = profileItem is null ? null : Payload<ResidentRegistration>(profileItem);
            if (profile is null || !OwnerApartments.CanManage(profile) || profile.BuildingId != building ||
                profile.UserId != owner || profile.Id != record.RegistrationId) continue;
            items.Add(new(profile.Id, profile.FirstName, profile.LastName,
                record.Items.Where(item => item.Status == ApartmentReviewStatus.Pending).ToList()));
        }
        return new(items, Encode(page.LastEvaluatedKey));
    }

    public async Task DecideApartmentAsync(AdminUser actor, string registrationId, string apartmentId, bool approved, CancellationToken ct)
    {
        if (!actor.IsActive || !actor.EffectivePermissions.PendingResidents)
            throw new ApiError(403, "Недостатньо прав для перевірки квартир.");
        var table = residentsTable ?? throw new InvalidOperationException("Missing residents table.");
        var profile = await GetCurrentRegisteredResidentAsync(registrationId, ct);
        if (profile is null || !OwnerApartments.CanManage(profile))
            throw new ApiError(404, "Профіль власника недоступний.");
        var record = await GetOwnerApartmentRecordAsync(profile.UserId, ct);
        var item = record?.RegistrationId == profile.Id ? record.Items.SingleOrDefault(value => value.Id == apartmentId) : null;
        if (record is null || item is null) throw new ApiError(404, "Квартиру не знайдено.");
        if (item.Status != ApartmentReviewStatus.Pending) throw new ApiError(409, "Рішення щодо цієї квартири вже ухвалено.");
        var now = DateTimeOffset.UtcNow;
        var reviewed = item with { Status = approved ? ApartmentReviewStatus.Approved : ApartmentReviewStatus.Rejected, ReviewedAt = now, ReviewedBy = actor.Email };
        var next = record with { Version = record.Version + 1, Items = record.Items.Select(value => value.Id == apartmentId ? reviewed : value).ToList() };
        var text = approved ? "Запит на додавання квартири підтверджено." : "Запит на додавання квартири відхилено.";
        await Transact([ActorCheck(actor),
            new() { ConditionCheck = OwnerApartments.CheckProfile(table, building, profile.UserId, profile.Version) },
            next.Save(table, record.Version),
            NotificationWrite(new ResidentNotification("apartment-" + item.Id, building, profile.UserId, null, null,
                text + " Перевірте статус у розділі «Мої квартири».", now, RegistrationId: profile.Id))], ct);
    }

    private async Task<OwnerApartments?> GetOwnerApartmentRecordAsync(long userId, CancellationToken ct)
    {
        var table = residentsTable ?? throw new InvalidOperationException("Missing residents table.");
        var row = await Get(table, new() { ["pk"] = S(OwnerApartments.Key(building, userId)) }, ct);
        return row is null ? null : OwnerApartments.Read(row, building, userId);
    }

    private bool TryReviewOwner(Dictionary<string, AttributeValue> key, out long owner) =>
        long.TryParse(key.GetValueOrDefault("recordOrder")?.S, NumberStyles.None, CultureInfo.InvariantCulture, out owner) &&
        owner > 0 && key.GetValueOrDefault("pk")?.S == OwnerApartments.Key(building, owner);
}
