using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Models;

public enum ReplyKeyboard
{
    RequestContact,
    Remove,
    RequestScope,
    RequestFloor,
    RequestCategory,
    RequestEntrance,
    RegistrationBuilding,
    ProfileDeleteConfirmation,
    OptionalEmail,
    PersonalDataConsent,
    ResidentType,
    OwnerRequestCategory,
    KeyType,
    MainGuest,
    MainResident
}

public sealed record RegistrationChange(ResidentRegistration Value, int? ExpectedVersion, bool AutomaticallyApproved = false);
