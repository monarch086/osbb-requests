using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services;

public interface IResidentStore
{
    Task<CompactResidentPage?> ListCompactResidentsAsync(int page, string? buildingNumber, string? residentType, string? direction, string? cursor, CancellationToken ct, bool byName = false);
    Task<ResidentDirectoryPage> ListResidentDirectoryAsync(bool pending, int page, string? buildingNumber, string? residentType, string? sort, string? direction, CancellationToken ct);
    Task<IReadOnlyList<ResidentRegistration>> ListResidentExportAsync(bool pending, string? buildingNumber, string? residentType, string? sort, string? direction, CancellationToken ct);
    Task<int> CountRegistrationsAsync(CancellationToken ct);
    Task<Page<ResidentRegistration>> ListRegistrationsAsync(string? cursor, CancellationToken ct);
    Task<Page<RegisteredResident>> ListResidentsAsync(string? cursor, CancellationToken ct);
    Task DecideAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct);
    Task<RegistrationApproval?> GetRegistrationApprovalAsync(string reference, CancellationToken ct);
    Task<ResidentRegistration?> GetCurrentRegisteredResidentAsync(string reference, CancellationToken ct);
    Task<ResidentRegistration?> GetRegistrationAsync(string reference, CancellationToken ct);
    Task UpdateResidentDataAsync(AdminUser actor, string reference, ResidentDataChangeRequest change, CancellationToken ct);
    Task ChangeResidentTypeAsync(AdminUser actor, string reference, ResidentTypeChangeRequest change, CancellationToken ct);
    Task<int> CountApartmentReviewsAsync(CancellationToken ct);
    Task<Page<ApartmentReviewEntry>> ListApartmentReviewsAsync(string? cursor, CancellationToken ct);
    Task DecideApartmentAsync(AdminUser actor, string registrationId, string apartmentId, bool approved, CancellationToken ct);
    Task<IReadOnlyList<OwnerApartment>> ListOwnerApartmentsAsync(ResidentRegistration profile, CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyList<OwnerApartment>>> ListApprovedApartmentsForExportAsync(IReadOnlyList<ResidentRegistration> residents, CancellationToken ct);
    Task<Page<ResidentTypeChange>> RegistrationHistoryAsync(string reference, string? cursor, CancellationToken ct);
    Task<ResidentKeyPage> ListResidentsWithKeysAsync(int page, CancellationToken ct);
    Task UpdateResidentKeyAsync(AdminUser actor, long ownerId, long requestId, int index, ResidentKeyUpdate change, CancellationToken ct);
}
