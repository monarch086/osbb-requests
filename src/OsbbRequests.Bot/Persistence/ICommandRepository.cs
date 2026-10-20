using OsbbRequests.Shared.Models;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Persistence;

public interface ICommandRepository
{
    Task<PublicRequestPage> ListPublicRequestsAsync(string complexId, string? before, CancellationToken cancellationToken);
    Task<DeletableRequest?> GetDeletableRequestAsync(long userId, long requestId, CancellationToken cancellationToken);
    Task<RequestLookup?> GetRequestLookupAsync(long userId, CancellationToken cancellationToken);
    Task<RequestDraft?> GetRequestDraftAsync(long userId, CancellationToken cancellationToken);
    Task<ResidentRegistration?> GetRegistrationAsync(long userId, CancellationToken cancellationToken);
    Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken cancellationToken);
    Task<MaintenanceRequest?> GetRequestAsync(long userId, long requestId, CancellationToken cancellationToken);
    Task<RequestPage> ListRequestsAsync(long userId, long? beforeId, CancellationToken cancellationToken);
    Task<StoredReply?> GetReplyAsync(long updateId, CancellationToken cancellationToken);
    // Atomically persists the request, initial history and reply; returns the winning reply on retries.
    Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome, CancellationToken cancellationToken);
    Task MarkDeliveredAsync(long updateId, CancellationToken cancellationToken);
}
