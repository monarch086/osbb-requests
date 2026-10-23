using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Data;

public interface IAdminStore
{
    Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct);
    Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct);
    Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct);
    Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct);
    Task<Page<ResidentRegistration>> ListRegistrationsAsync(string? cursor, CancellationToken ct);
    Task<Page<RegisteredResident>> ListResidentsAsync(string? cursor, CancellationToken ct);
    Task DecideAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct);
    Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct);
    Task<ManagedRequest?> GetRequestAsync(long ownerId, long requestId, CancellationToken ct);
    Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct);
    Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(MaintenanceRequest request, CancellationToken ct);
}
