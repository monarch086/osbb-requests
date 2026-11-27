using System.Text.Json.Serialization;

namespace OsbbRequests.Services.Residents;

[JsonConverter(typeof(JsonStringEnumConverter<RegistrationStage>))]
public enum RegistrationStage
{
    AwaitingPhone,
    AwaitingFirstName,
    AwaitingLastName,
    Pending,
    Approved,
    Rejected,
    Cancelled,
    AwaitingBuilding,
    AwaitingApartment,
    Deleted,
    AwaitingEmail,
    AwaitingConsent,
    AwaitingResidentType,
    AwaitingInitialResidentType
}
