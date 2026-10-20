using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Models;

public sealed record CommandOutcome(string Reply, MaintenanceRequest? NewRequest = null,
    RegistrationChange? Registration = null, ReplyKeyboard? Keyboard = null, RequestDraftChange? Draft = null,
    RequestLookupChange? Lookup = null, RequestDeletion? Deletion = null);
public sealed record StoredReply(long UpdateId, long UserId, long ChatId, string Text, bool Delivered,
    ReplyKeyboard? Keyboard = null);
