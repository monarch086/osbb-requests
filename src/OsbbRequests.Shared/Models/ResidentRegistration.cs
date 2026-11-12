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
    Deleted,
    AwaitingEmail,
    AwaitingConsent,
    AwaitingResidentType,
    AwaitingInitialResidentType
}

[JsonConverter(typeof(JsonStringEnumConverter<ResidentType>))]
public enum ResidentType { FamilyMember, Owner, Tenant, Employee }

public sealed record ResidentRegistration(
    string Id, long UserId, string BuildingId, RegistrationStage Stage, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Phone = null, string? FirstName = null, string? LastName = null, string? DecisionReason = null,
    string? BuildingNumber = null, string? ApartmentNumber = null, bool PersonalDataDeleted = false, DateTimeOffset? PersonalDataDeletedAt = null, DateTimeOffset? ApprovedAt = null, string? Email = null, DateTimeOffset? ConsentAt = null, string? ConsentVersion = null, RegistrationStage? ResumeAfterConsent = null, ResidentType ResidentType = ResidentType.FamilyMember, bool TypeBeforeAddress = false, int TypeChangeCount = 0)
{
    [JsonIgnore]
    public bool HasDeletedPersonalData => PersonalDataDeleted || Stage == RegistrationStage.Deleted ||
        PersonalDataDeletedAt is not null && Stage != RegistrationStage.Approved;
}
