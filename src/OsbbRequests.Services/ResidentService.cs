using System.Globalization;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Admin;
using OsbbRequests.Services.Export;

namespace OsbbRequests.Services;

public sealed class ResidentService(IResidentStore store)
{
    public Task<ResidentKeyPage> ListResidentsWithKeysAsync(AdminUser actor, string? page, CancellationToken ct)
    {
        RequireKeyRead(actor);
        if (!int.TryParse(page ?? "1", out var number) || number < 1 || number > 1000000)
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректна сторінка.");
        return store.ListResidentsWithKeysAsync(number, ct);
    }

    public static void RequireKeyEdit(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.KeyOrders)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для редагування ключів.");
    }

    public Task UpdateResidentKeyAsync(AdminUser actor, long ownerId, long requestId, int position, ResidentKeyUpdate change, CancellationToken ct)
    {
        RequireKeyEdit(actor);
        return store.UpdateResidentKeyAsync(actor, ownerId, requestId, position, change, ct);
    }

    public static void RequireResidentDirectoryAccess(AdminUser actor, bool pending)
    {
        AdminAccess.RequireActive(actor);
        if (pending ? !actor.EffectivePermissions.PendingResidents : !actor.EffectivePermissions.RegisteredResidents)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перегляду мешканців.");
    }

    public Task<byte[]> ExportResidentsAsync(AdminUser actor, bool pending, ResidentDirectoryFilters filters, CancellationToken ct)
    {
        RequireResidentDirectoryAccess(actor, pending);
        return new ResidentExport(store).CreateAsync(pending, filters.Building, filters.ResidentType, filters.Sort, filters.Direction, ct);
    }

    public async Task<IResidentDirectoryView> ListResidentDirectoryAsync(AdminUser actor, bool pending, ResidentDirectoryFilters filters,
        string? page, string? cursor, bool compact, CancellationToken ct)
    {
        RequireResidentDirectoryAccess(actor, pending);
        if (!int.TryParse(page ?? "1", out var number))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректна сторінка.");
        if (!pending && compact && filters.Sort is null or "" or "registeredAt" or "lastName")
        {
            var compactPage = await store.ListCompactResidentsAsync(number, filters.Building, filters.ResidentType,
                filters.Direction, cursor, ct, byName: filters.Sort == "lastName");
            if (compactPage is not null) return compactPage;
        }
        var directory = await store.ListResidentDirectoryAsync(pending, number, filters.Building, filters.ResidentType, filters.Sort, filters.Direction, ct);
        return pending
            ? new ResidentDirectoryView<PendingResident>(directory.Items.Select(ToPendingResident).ToArray(), directory.PageNumber, directory.PageSize, directory.Total, directory.Counts)
            : new ResidentDirectoryView<RegisteredResident>(directory.Items.Select(ToRegisteredResident).ToArray(), directory.PageNumber, directory.PageSize, directory.Total, directory.Counts);
    }

    public Task<Page<RegisteredResident>> ListResidentsAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireResidentDirectoryAccess(actor, pending: false);
        return store.ListResidentsAsync(cursor, ct);
    }

    public static void RequireRegistrationReview(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.PendingResidents)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перевірки реєстрацій.");
    }

    public static void ValidateRegistrationReference(string reference)
    {
        if (reference.Length != 32 || !reference.All(Uri.IsHexDigit))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний запит.");
    }

    public Task<int> CountRegistrationsAsync(AdminUser actor, CancellationToken ct)
    {
        RequireRegistrationReview(actor);
        return store.CountRegistrationsAsync(ct);
    }

    public async Task<Page<PendingResident>> ListRegistrationsAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireRegistrationReview(actor);
        var page = await store.ListRegistrationsAsync(cursor, ct);
        return new(page.Items.Select(ToPendingResident).ToArray(), page.Cursor);
    }

    public Task UpdateResidentDataAsync(AdminUser actor, string reference, ResidentDataChangeRequest change, CancellationToken ct)
    {
        RequireRegistrationReview(actor);
        ValidateRegistrationReference(reference);
        return store.UpdateResidentDataAsync(actor, reference, change, ct);
    }

    public Task ChangeResidentTypeAsync(AdminUser actor, string reference, ResidentTypeChangeRequest change, CancellationToken ct)
    {
        RequireRegistrationReview(actor);
        ValidateRegistrationReference(reference);
        return store.ChangeResidentTypeAsync(actor, reference, change, ct);
    }

    public static void RequireApartmentReview(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.PendingResidents)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перевірки квартир.");
    }

    public Task<int> CountApartmentReviewsAsync(AdminUser actor, CancellationToken ct)
    {
        RequireApartmentReview(actor);
        return store.CountApartmentReviewsAsync(ct);
    }

    public Task<Page<ApartmentReviewEntry>> ListApartmentReviewsAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireApartmentReview(actor);
        return store.ListApartmentReviewsAsync(cursor, ct);
    }

    public Task DecideApartmentAsync(AdminUser actor, string owner, string apartment, ApartmentReviewDecision decision, CancellationToken ct)
    {
        RequireApartmentReview(actor);
        ValidateRegistrationReference(owner);
        ValidateRegistrationReference(apartment);
        if (decision.Approved is not { } approved)
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний запит.");
        return store.DecideApartmentAsync(actor, owner, apartment, approved, ct);
    }

    public async Task<RegisteredResidentDetails> GetRegisteredResidentAsync(AdminUser actor, string reference, CancellationToken ct)
    {
        RequireResidentDirectoryAccess(actor, pending: false);
        ValidateRegistrationReference(reference);
        var resident = await store.GetCurrentRegisteredResidentAsync(reference, ct) ??
            throw new AdminOperationException(AdminFailure.NotFound, "Реєстрацію не знайдено.");
        var approval = await store.GetRegistrationApprovalAsync(reference, ct);
        return new(ToRegisteredResident(resident), approval, await store.ListOwnerApartmentsAsync(resident, ct));
    }

    public async Task<RegistrationApproval?> GetRegistrationApprovalAsync(AdminUser actor, string reference, CancellationToken ct)
    {
        RequireResidentDirectoryAccess(actor, pending: false);
        var registration = await GetRegistrationAsync(reference, ct);
        if (registration.Status != ResidentRegistrationStatus.Approved || registration.PersonalDataDeleted)
            throw new AdminOperationException(AdminFailure.NotFound, "Реєстрацію не знайдено.");
        return await store.GetRegistrationApprovalAsync(reference, ct);
    }

    public async Task<Page<ResidentTypeChange>> GetRegistrationHistoryAsync(AdminUser actor, string reference, string? cursor, CancellationToken ct)
    {
        RequireRegistrationHistoryAccess(actor);
        var registration = await GetRegistrationAsync(reference, ct);
        if (registration.Status == ResidentRegistrationStatus.Pending ? !actor.EffectivePermissions.PendingResidents :
            registration.Status == ResidentRegistrationStatus.Approved ? !actor.EffectivePermissions.RegisteredResidents : true)
        {
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перегляду історії.");
        }

        return await store.RegistrationHistoryAsync(reference, cursor, ct);
    }

    public Task DecideRegistrationAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct)
    {
        RequireRegistrationReview(actor);
        ValidateRegistrationReference(reference);
        return store.DecideAsync(actor, reference, decision, ct);
    }

    private async Task<ResidentProfile> GetRegistrationAsync(string reference, CancellationToken ct)
    {
        ValidateRegistrationReference(reference);
        return await store.GetRegistrationAsync(reference, ct) ??
            throw new AdminOperationException(AdminFailure.NotFound, "Реєстрацію не знайдено.");
    }

    private static PendingResident ToPendingResident(ResidentProfile resident) => new(resident.Id, resident.FirstName, resident.LastName,
        resident.Phone, resident.Email, resident.BuildingNumber, resident.ApartmentNumber, resident.ResidentType,
        resident.TypeChangeCount, resident.ConsentAt, resident.UpdatedAt, resident.Version, resident.UserId.ToString(CultureInfo.InvariantCulture));

    private static RegisteredResident ToRegisteredResident(ResidentProfile resident) => new(resident.Id, resident.FirstName, resident.LastName,
        resident.Phone, resident.ApprovedAt ?? resident.UpdatedAt, resident.UserId.ToString(CultureInfo.InvariantCulture), resident.BuildingNumber,
        resident.ApartmentNumber, resident.PersonalDataDeleted, resident.Email, resident.ConsentAt, resident.ResidentType, resident.TypeChangeCount, resident.Version);

    public static void RequireKeyRead(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.KeyOrders)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перегляду замовлень ключів.");
    }

    public static void RequireRegistrationHistoryAccess(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.PendingResidents && !actor.EffectivePermissions.RegisteredResidents)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перегляду історії.");
    }
}
