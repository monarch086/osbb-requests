using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services;

public interface IAdminStore
{
    Task<CompactResidentPage?> ListCompactResidentsAsync(int page, string? buildingNumber, string? residentType, string? direction, string? cursor, CancellationToken ct, bool byName = false);
    Task<ResidentDirectoryPage> ListResidentDirectoryAsync(bool pending, int page, string? buildingNumber, string? residentType, string? sort, string? direction, CancellationToken ct);
    Task<IReadOnlyList<ResidentRegistration>> ListResidentExportAsync(bool pending, string? buildingNumber, string? residentType, string? sort, string? direction, CancellationToken ct);
    Task<Page<BroadcastMessage>> ListMessagesAsync(string? cursor, CancellationToken ct);
    Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct);
    Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct);
    Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct);
    Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct);
    Task SetNotificationSettingsAsync(AdminUser actor, AdminUser target, NotificationSettingsChange change, CancellationToken ct);
    Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct);
    Task SetPermissionsAsync(AdminUser actor, AdminUser target, PermissionsChange change, CancellationToken ct);
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
    Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct);
    Task<AdminRequestPage> ListRequestsWithStatisticsAsync(RequestFilters filters, string? cursor, int pageNumber, CancellationToken ct, bool includeCounts = true);
    Task<ManagedRequest?> GetRequestAsync(long ownerId, long requestId, CancellationToken ct);
    Task<IReadOnlyList<IReadOnlyList<KeyResident>>> GetKeyResidentsAsync(MaintenanceRequest request, CancellationToken ct);
    Task<ResidentKeyPage> ListResidentsWithKeysAsync(int page, CancellationToken ct);
    Task UpdateResidentKeyAsync(AdminUser actor, long ownerId, long requestId, int index, ResidentKeyUpdate change, CancellationToken ct);
    Task<RequestAuthor?> GetRequestAuthorAsync(MaintenanceRequest request, CancellationToken ct);
    Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct, string? comment = null);
    Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(MaintenanceRequest request, CancellationToken ct);
}
