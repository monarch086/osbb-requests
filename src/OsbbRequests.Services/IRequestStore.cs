using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services;

public interface IRequestStore
{
    Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct);
    Task<AdminRequestPage> ListRequestsWithStatisticsAsync(RequestFilters filters, string? cursor, int pageNumber, CancellationToken ct, bool includeCounts = true);
    Task<ManagedRequest?> GetRequestAsync(long ownerId, long requestId, CancellationToken ct);
    Task<IReadOnlyList<IReadOnlyList<KeyResident>>> GetKeyResidentsAsync(MaintenanceRequest request, CancellationToken ct);
    Task<RequestAuthor?> GetRequestAuthorAsync(MaintenanceRequest request, CancellationToken ct);
    Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct, string? comment = null);
    Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(MaintenanceRequest request, CancellationToken ct);
}
