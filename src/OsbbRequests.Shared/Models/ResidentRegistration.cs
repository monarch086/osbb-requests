using System.Text.Json.Serialization;

namespace OsbbRequests.Shared.Models;

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
    Deleted
}

public sealed record ResidentRegistration(
    string Id, long UserId, string BuildingId, RegistrationStage Stage, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Phone = null, string? FirstName = null, string? LastName = null, string? DecisionReason = null,
    string? BuildingNumber = null, string? ApartmentNumber = null, bool PersonalDataDeleted = false, DateTimeOffset? PersonalDataDeletedAt = null, DateTimeOffset? ApprovedAt = null)
{
    [JsonIgnore]
    public bool HasDeletedPersonalData => PersonalDataDeleted || Stage == RegistrationStage.Deleted ||
        PersonalDataDeletedAt is not null && Stage != RegistrationStage.Approved;
}
