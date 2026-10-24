using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Models;

public enum ReplyKeyboard
{
    RequestContact,
    Remove,
    RequestScope,
    RequestFloor,
    RequestCategory,
    RequestEntrance
}

public sealed record RegistrationChange(ResidentRegistration Value, int? ExpectedVersion);
