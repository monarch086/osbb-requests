using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

public interface IMessageStore
{
    Task<Page<BroadcastMessage>> ListMessagesAsync(string? cursor, CancellationToken ct);
    Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct);
}
