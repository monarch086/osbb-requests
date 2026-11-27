using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Residents;

namespace OsbbRequests.CommandProcessor.Persistence;

public interface ICommandRepository
{
    Task<long?> GetNotificationRequestAsync(long userId, long messageId, CancellationToken cancellationToken) => Task.FromResult<long?>(null);
    Task<PageNavigation?> GetPageNavigationAsync(long userId, CancellationToken cancellationToken);
    Task<string?> GetComplexCursorAsync(long userId, string complexId, string token, CancellationToken cancellationToken);
    Task<OwnerApartments?> GetOwnerApartmentsAsync(long userId, CancellationToken ct);
    Task<ResidentProfileDialog?> GetProfileDialogAsync(long userId, CancellationToken cancellationToken);
    IAsyncEnumerable<RequestListItem> ReadRequestListAsync(string complexId, long? ownerId, RequestListBoundary? before, long[]? pageIds, CancellationToken cancellationToken);
    Task<DeletableRequest?> GetDeletableRequestAsync(long userId, long requestId, CancellationToken cancellationToken);
    Task<RequestLookup?> GetRequestLookupAsync(long userId, CancellationToken cancellationToken);
    Task<RequestDraft?> GetRequestDraftAsync(long userId, CancellationToken cancellationToken);
    Task<bool> ResidentPhoneExistsAsync(string complexId, string phone, CancellationToken cancellationToken);
    Task<ResidentRegistration?> GetRegistrationAsync(long userId, CancellationToken cancellationToken);
    Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken cancellationToken);
    Task<MaintenanceRequest?> GetRequestAsync(long userId, long requestId, CancellationToken cancellationToken);
    Task<StoredReply?> GetReplyAsync(long updateId, CancellationToken cancellationToken);
    // Atomically persists the request, initial history and reply; returns the winning reply on retries.
    Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome, CancellationToken cancellationToken);
    Task MarkDeliveredAsync(long updateId, CancellationToken cancellationToken);
}
