using System.Text.Json.Serialization;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services.Residents;

// Keep the flat shape of drafts already persisted by the Telegram listener.
public sealed record ResidentRegistration(
    string Id, long UserId, string BuildingId, RegistrationStage Stage, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Phone = null, string? FirstName = null, string? LastName = null, string? DecisionReason = null,
    string? BuildingNumber = null, string? ApartmentNumber = null, bool PersonalDataDeleted = false,
    DateTimeOffset? PersonalDataDeletedAt = null, DateTimeOffset? ApprovedAt = null, string? Email = null,
    DateTimeOffset? ConsentAt = null, string? ConsentVersion = null, RegistrationStage? ResumeAfterConsent = null,
    ResidentType ResidentType = ResidentType.FamilyMember, bool TypeBeforeAddress = false, int TypeChangeCount = 0)
    : ResidentProfile(Id, UserId, BuildingId, Version, CreatedAt, UpdatedAt, Phone, FirstName, LastName, DecisionReason,
        BuildingNumber, ApartmentNumber, PersonalDataDeleted, PersonalDataDeletedAt, ApprovedAt, Email, ConsentAt,
        ConsentVersion, ResidentType, TypeChangeCount)
{
    [JsonIgnore]
    public override ResidentRegistrationStatus Status => Stage switch
    {
        RegistrationStage.Pending => ResidentRegistrationStatus.Pending,
        RegistrationStage.Approved => ResidentRegistrationStatus.Approved,
        RegistrationStage.Rejected => ResidentRegistrationStatus.Rejected,
        RegistrationStage.Cancelled => ResidentRegistrationStatus.Cancelled,
        RegistrationStage.Deleted => ResidentRegistrationStatus.Deleted,
        _ => ResidentRegistrationStatus.Draft
    };

    [JsonIgnore]
    public override bool HasDeletedPersonalData => base.HasDeletedPersonalData;
}
