using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task<int> CountApartmentReviewsAsync(CancellationToken ct) =>
        Read((db, current) => PendingApartments(db, current).CountAsync(ct), ct);

    public Task<Page<ApartmentReviewEntry>> ListApartmentReviewsAsync(string? cursor, CancellationToken ct) => Read(async (db, current) =>
    {
        var offset = Offset(cursor);
        var ownerIds = await PendingApartments(db, current).Select(a => a.TelegramUserId).Distinct().OrderBy(id => id)
            .Skip(offset).Take(PageSize + 1).ToArrayAsync(ct);
        var ids = ownerIds.Take(PageSize).ToArray();
        var rows = await (from apartment in PendingApartments(db, current)
                          join owner in ResidentRows(db, current, pending: false) on apartment.TelegramUserId equals owner.TelegramUserId
                          where ids.Contains(apartment.TelegramUserId) && owner.RegistrationId == apartment.RegistrationId
                          orderby apartment.TelegramUserId, apartment.SubmittedAt, apartment.ApartmentId
                          select new { Apartment = apartment, owner.FirstName, owner.LastName }).ToListAsync(ct);
        var items = rows.GroupBy(x => (x.Apartment.RegistrationId, x.FirstName, x.LastName)).Select(g =>
            new ApartmentReviewEntry(g.Key.RegistrationId, g.Key.FirstName, g.Key.LastName, g.Select(x => ToApartment(x.Apartment)).ToArray())).ToArray();
        return new Page<ApartmentReviewEntry>(items, NextCursor(offset, ownerIds.Length));
    }, ct);

    public Task<IReadOnlyList<OwnerApartment>> ListOwnerApartmentsAsync(ResidentRegistration profile, CancellationToken ct) =>
        Read<IReadOnlyList<OwnerApartment>>(async (db, area) =>
        {
            if (!IsOwner(profile) || profile.BuildingId != options.Complex) return [];
            var apartments = await db.ResidentApartments.Where(row => row.ComplexId == area.ComplexId &&
                row.TelegramUserId == profile.UserId && row.RegistrationId == profile.Id).OrderBy(row => row.SubmittedAt).ToListAsync(ct);
            return apartments.Select(ToApartment).ToArray();
        }, ct);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<OwnerApartment>>> ListApprovedApartmentsForExportAsync(
        IReadOnlyList<ResidentRegistration> residents, CancellationToken ct) => Read<IReadOnlyDictionary<string, IReadOnlyList<OwnerApartment>>>(async (db, area) =>
    {
        var ids = residents.Where(row => IsOwner(row) && row.BuildingId == options.Complex).Select(row => row.Id).ToArray();
        var rows = await db.ResidentApartments.Where(row => row.ComplexId == area.ComplexId && ids.Contains(row.RegistrationId) &&
            row.Status == ApartmentReviewStatus.Approved).OrderBy(row => row.SubmittedAt).ToListAsync(ct);
        return rows.GroupBy(row => row.RegistrationId).ToDictionary(g => g.Key, g => (IReadOnlyList<OwnerApartment>)g.Select(ToApartment).ToArray());
    }, ct);

    public Task DecideApartmentAsync(AdminUser actor, string registrationId, string apartmentId, bool approved, CancellationToken ct) =>
        Write(async (db, area) =>
        {
            ResidentService.RequireApartmentReview(actor);
            await CheckActor(db, area, actor, ct);
            var owner = await ResidentRows(db, area, false).SingleOrDefaultAsync(row =>
                row.RegistrationId == registrationId && row.ResidentType == ResidentType.Owner, ct)
                ?? throw Error(AdminFailure.NotFound, "Профіль власника недоступний.");
            var row = await db.ResidentApartments.SingleOrDefaultAsync(row => row.ComplexId == area.ComplexId &&
                row.TelegramUserId == owner.TelegramUserId && row.RegistrationId == registrationId && row.ApartmentId == apartmentId, ct)
                ?? throw Error(AdminFailure.NotFound, "Квартиру не знайдено.");
            if (row.Status != ApartmentReviewStatus.Pending)
                throw Error(AdminFailure.Conflict, "Рішення щодо цієї квартири вже ухвалено.");
            var now = DateTimeOffset.UtcNow;
            row.Status = approved ? ApartmentReviewStatus.Approved : ApartmentReviewStatus.Rejected;
            row.ReviewedAt = now;
            row.ReviewedBy = actor.Email;
            var text = approved ? "Запит на додавання квартири підтверджено." : "Запит на додавання квартири відхилено.";
            Enqueue(db, area, new ResidentNotification("apartment-" + apartmentId, options.Complex, owner.TelegramUserId, null, null,
                text + " Перевірте статус у розділі «Мої квартири».", now, RegistrationId: registrationId));
        }, ct);

    private static IQueryable<ResidentApartment> PendingApartments(DataContext db, Scope area) =>
        db.ResidentApartments.Where(apartment => apartment.ComplexId == area.ComplexId && apartment.Status == ApartmentReviewStatus.Pending &&
            ResidentRows(db, area, false).Any(owner => owner.TelegramUserId == apartment.TelegramUserId &&
                owner.RegistrationId == apartment.RegistrationId && owner.ResidentType == ResidentType.Owner));

    private static bool IsOwner(ResidentRegistration profile) =>
        profile is { Stage: RegistrationStage.Approved, ResidentType: ResidentType.Owner, PersonalDataDeleted: false };

    private static OwnerApartment ToApartment(ResidentApartment row) => new(row.ApartmentId, row.BuildingNumber, row.ApartmentNumber,
        row.SubmittedAt, row.Status, row.ReviewedAt, row.ReviewedBy);
}
