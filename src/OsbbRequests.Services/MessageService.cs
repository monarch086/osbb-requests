using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

public sealed class MessageService(IMessageStore store)
{
    public static void RequireMessagesAccess(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.Messages)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для роботи з повідомленнями.");
    }

    public Task<Page<BroadcastMessage>> ListMessagesAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireMessagesAccess(actor);
        return store.ListMessagesAsync(cursor, ct);
    }

    public Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct)
    {
        RequireMessagesAccess(actor);
        return store.SendMessageAsync(actor, change, ct);
    }
}
