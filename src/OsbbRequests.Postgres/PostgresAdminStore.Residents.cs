using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task<CompactResidentPage?> ListCompactResidentsAsync(int page, string? buildingNumber, string? residentType,
        string? direction, string? cursor, CancellationToken ct, bool byName = false) =>
        Read<CompactResidentPage?>(async (db, current) =>
        {
            var directory = await ReadDirectoryAsync(db, current, false, page, buildingNumber, residentType,
                byName ? "lastName" : "registeredAt", direction, ct);
            var offset = (directory.PageNumber - 1) * PageSize;
            var items = directory.Items.Select(r => new ResidentListRow(r.Id, r.FirstName, r.LastName, r.ResidentType, r.BuildingNumber, r.ApartmentNumber)).ToArray();
            return new(items, directory.PageNumber, PageSize, directory.Total, directory.Counts,
                offset > 0 ? offset.ToString(CultureInfo.InvariantCulture) : null,
                offset + PageSize < directory.Total ? (offset + PageSize).ToString(CultureInfo.InvariantCulture) : null);
        }, ct);

    public Task<ResidentDirectoryPage> ListResidentDirectoryAsync(bool pending, int page, string? buildingNumber,
        string? residentType, string? sort, string? direction, CancellationToken ct) =>
        Read((db, current) => ReadDirectoryAsync(db, current, pending, page, buildingNumber, residentType, sort, direction, ct), ct);

    public Task<IReadOnlyList<ResidentRegistration>> ListResidentExportAsync(bool pending, string? buildingNumber,
        string? residentType, string? sort, string? direction, CancellationToken ct) => Read<IReadOnlyList<ResidentRegistration>>(async (db, current) =>
    {
        var (query, _) = FilterResidents(db, current, pending, buildingNumber, residentType, sort, direction);
        var rows = await SortResidents(query, pending, direction).Take(1_048_576).ToListAsync(ct);
        if (rows.Count > 1_048_575) throw Error(AdminFailure.TooLarge, "Забагато мешканців. Звузьте фільтри та спробуйте ще раз.");
        if (!pending && sort == "lastName") SortNames(rows, direction);
        return await ToProfilesAsync(db, rows, ct);
    }, ct);

    private async Task<ResidentDirectoryPage> ReadDirectoryAsync(DataContext db, Scope current, bool pending, int page,
        string? building, string? type, string? sort, string? direction, CancellationToken ct)
    {
        if (page is < 1 or > 1000000) throw Error(AdminFailure.InvalidInput, "Некоректна сторінка.");
        var (query, countsQuery) = FilterResidents(db, current, pending, building, type, sort, direction);
        var groups = await countsQuery.GroupBy(row => row.ResidentType).Select(group => new { Type = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.Type, group => group.Count, ct);
        var counts = new ResidentCounts(groups.Values.Sum(), groups.GetValueOrDefault(ResidentType.Owner),
            groups.GetValueOrDefault(ResidentType.FamilyMember), groups.GetValueOrDefault(ResidentType.Tenant), groups.GetValueOrDefault(ResidentType.Employee));
        var total = string.IsNullOrEmpty(type) ? counts.Total : await query.CountAsync(ct);
        var actual = PageNumber(page, total);
        var offset = (actual - 1) * PageSize;
        List<Resident> rows;
        if (!pending && sort == "lastName")
        {
            rows = await query.ToListAsync(ct);
            SortNames(rows, direction);
            rows = rows.Skip(offset).Take(PageSize).ToList();
        }
        else
            rows = await SortResidents(query, pending, direction).Skip(offset).Take(PageSize).ToListAsync(ct);
        return new(await ToProfilesAsync(db, rows, ct), actual, PageSize, total, counts);
    }

    private static (IQueryable<Resident> Filtered, IQueryable<Resident> Counts) FilterResidents(DataContext db, Scope current,
        bool pending, string? building, string? residentType, string? sort, string? direction)
    {
        if ((building is not (null or "" or "Unspecified") && !ComplexLayout.BuildingNumbers.Contains(building)) ||
            (residentType is not (null or "") && !Enum.GetNames<ResidentType>().Contains(residentType)) ||
            sort is not (null or "" or "registeredAt" or "lastName") || direction is not (null or "" or "asc" or "desc"))
            throw Error(AdminFailure.InvalidInput, "Некоректні параметри списку мешканців.");
        var query = ResidentRows(db, current, pending);
        if (building == "Unspecified") query = query.Where(row => row.BuildingNumber == null || row.BuildingNumber == "");
        else if (!string.IsNullOrEmpty(building)) query = query.Where(row => row.BuildingNumber == building);
        if (string.IsNullOrEmpty(residentType)) return (query, query);
        var type = Enum.Parse<ResidentType>(residentType);
        return (query.Where(row => row.ResidentType == type), query);
    }

    private static IOrderedQueryable<Resident> SortResidents(IQueryable<Resident> query, bool pending, string? direction) =>
        pending ? query.OrderBy(row => row.RegistrationId) : (direction == "asc"
            ? query.OrderBy(row => row.ApprovedAt ?? row.UpdatedAt)
            : query.OrderByDescending(row => row.ApprovedAt ?? row.UpdatedAt)).ThenBy(row => row.RegistrationId);

    private static void SortNames(List<Resident> rows, string? direction) => rows.Sort((a, b) =>
        ResidentNameOrder.Compare(a.RegistrationId!, a.FirstName, a.LastName, b.RegistrationId!, b.FirstName, b.LastName, direction == "asc"));

    private static IQueryable<Resident> ResidentRows(DataContext db, Scope current, bool pending)
    {
        var stage = pending ? RegistrationStage.Pending : RegistrationStage.Approved;
        return db.Residents.Where(row => row.ComplexId == current.ComplexId && row.RegistrationId != null && row.Stage == stage && !row.PersonalDataDeleted);
    }

    public Task<int> CountRegistrationsAsync(CancellationToken ct) =>
        Read((db, current) => ResidentRows(db, current, pending: true).CountAsync(ct), ct);

    public Task<Page<ResidentRegistration>> ListRegistrationsAsync(string? cursor, CancellationToken ct) =>
        ListProfilesAsync(true, cursor, ct);

    public async Task<Page<RegisteredResident>> ListResidentsAsync(string? cursor, CancellationToken ct)
    {
        var page = await ListProfilesAsync(false, cursor, ct);
        return new(page.Items.Select(r => new RegisteredResident(r.Id, r.FirstName, r.LastName, r.Phone,
            r.ApprovedAt ?? r.UpdatedAt, r.UserId.ToString(CultureInfo.InvariantCulture), r.BuildingNumber,
            r.ApartmentNumber, r.PersonalDataDeleted, r.Email, r.ConsentAt, r.ResidentType, r.TypeChangeCount, r.Version)).ToArray(), page.Cursor);
    }

    private Task<Page<ResidentRegistration>> ListProfilesAsync(bool pending, string? cursor, CancellationToken ct) =>
        Read(async (db, current) =>
        {
            var offset = Offset(cursor);
            var rows = await SortResidents(ResidentRows(db, current, pending), pending, null).Skip(offset).Take(PageSize + 1).ToListAsync(ct);
            return new Page<ResidentRegistration>(await ToProfilesAsync(db, rows.Take(PageSize).ToArray(), ct), NextCursor(offset, rows.Count));
        }, ct);

    public Task<ResidentRegistration?> GetCurrentRegisteredResidentAsync(string reference, CancellationToken ct) =>
        Read((db, current) => FindProfileAsync(db, ResidentRows(db, current, pending: false).Where(row => row.RegistrationId == reference), ct), ct);

    public Task<ResidentRegistration?> GetRegistrationAsync(string reference, CancellationToken ct) =>
        Read((db, current) => FindProfileAsync(db, db.Residents.Where(row => row.ComplexId == current.ComplexId && row.RegistrationId == reference), ct), ct);

    private Task<ResidentRegistration?> ResidentByUserAsync(DataContext db, Scope current, long user, CancellationToken ct) =>
        FindProfileAsync(db, db.Residents.Where(row => row.ComplexId == current.ComplexId && row.TelegramUserId == user), ct);

    private async Task<ResidentRegistration?> FindProfileAsync(DataContext db, IQueryable<Resident> query, CancellationToken ct)
    {
        var row = await query.SingleOrDefaultAsync(ct);
        return row is null ? null : (await ToProfilesAsync(db, [row], ct))[0];
    }

    private async Task<ResidentRegistration[]> ToProfilesAsync(DataContext db, IReadOnlyCollection<Resident> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(row => row.Id).ToArray();
        var phones = await db.ResidentPhones.Where(phone => ids.Contains(phone.ResidentId) && phone.IsPrimary)
            .ToDictionaryAsync(phone => phone.ResidentId, phone => phone.Phone, ct);
        var consents = await db.ResidentConsents.Where(consent => ids.Contains(consent.ResidentId)).ToDictionaryAsync(consent => consent.ResidentId, ct);
        return rows.Select(r =>
        {
            var consent = consents.GetValueOrDefault(r.Id);
            return new ResidentRegistration(r.RegistrationId ?? "", r.TelegramUserId, options.Complex, r.Stage, r.Version,
                r.CreatedAt ?? DateTimeOffset.UnixEpoch, r.UpdatedAt ?? r.CreatedAt ?? DateTimeOffset.UnixEpoch,
                phones.GetValueOrDefault(r.Id), r.FirstName, r.LastName, r.DecisionReason, r.BuildingNumber, r.ApartmentNumber,
                r.PersonalDataDeleted, r.PersonalDataDeletedAt, r.ApprovedAt, r.Email, consent?.ConsentedAt, consent?.Version,
                r.ResumeAfterConsent, r.ResidentType, r.TypeBeforeAddress, r.TypeChangeCount);
        }).ToArray();
    }

    public Task<RegistrationApproval?> GetRegistrationApprovalAsync(string reference, CancellationToken ct) =>
        Read<RegistrationApproval?>(async (db, current) =>
        {
            var decision = await ResidentEvents(db, current, reference).Where(row => row.EventKey == "EVENT#DECIDED")
                .OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(ct);
            if (decision?.Actor is { Length: > 0 } actor)
            {
                using var data = JsonDocument.Parse(decision.Data);
                if (String(data.RootElement, "decision") == "Approved") return new(actor, decision.CreatedAt, actor == "system");
            }
            var membership = await db.ResidentMemberships.FirstOrDefaultAsync(row => row.SourceId == current.SourceId &&
                row.ComplexId == current.ComplexId && row.RegistrationId == reference && row.Active, ct);
            return membership?.ReviewedBy is { } reviewer ? new(reviewer, membership.ReviewedAt, reviewer == "system") : null;
        }, ct);

    public Task<Page<ResidentTypeChange>> RegistrationHistoryAsync(string reference, string? cursor, CancellationToken ct) =>
        Read(async (db, current) =>
        {
            var offset = Offset(cursor);
            var rows = await ResidentEvents(db, current, reference).Where(row => row.EventKey.StartsWith("EVENT#TYPE#"))
                .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.EventKey).Skip(offset).Take(PageSize + 1).ToListAsync(ct);
            var items = rows.Take(PageSize).Select(row =>
            {
                using var data = JsonDocument.Parse(row.Data);
                return JsonSerializer.Deserialize<ResidentTypeChange>(String(data.RootElement, "payload") ?? row.Data)!;
            }).ToArray();
            return new Page<ResidentTypeChange>(items, NextCursor(offset, rows.Count));
        }, ct);

    private IQueryable<ResidentEvent> ResidentEvents(DataContext db, Scope current, string reference) =>
        db.ResidentEvents.Where(row => row.SourceId == current.SourceId && row.RegistrationId == reference &&
            (row.Key.StartsWith("LOCAL#") || row.Key.StartsWith("COMPLEX#" + options.Complex + "#")));

    private static string ResidentName(ResidentRegistration profile)
    {
        var name = string.Join(" ", new[] { profile.FirstName, profile.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return name.Length > 0 ? name : "Мешканець";
    }
}
