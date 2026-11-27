namespace OsbbRequests.Domain.Models;

public enum ResidentRegistrationStatus { Draft, Pending, Approved, Rejected, Cancelled, Deleted }

public abstract record ResidentProfile(
    string Id, long UserId, string BuildingId, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Phone, string? FirstName, string? LastName, string? DecisionReason,
    string? BuildingNumber, string? ApartmentNumber, bool PersonalDataDeleted,
    DateTimeOffset? PersonalDataDeletedAt, DateTimeOffset? ApprovedAt, string? Email,
    DateTimeOffset? ConsentAt, string? ConsentVersion, ResidentType ResidentType, int TypeChangeCount)
{
    public abstract ResidentRegistrationStatus Status { get; }

    public bool IsApprovedOwner() =>
        Status == ResidentRegistrationStatus.Approved && ResidentType == ResidentType.Owner && !PersonalDataDeleted;

    public virtual bool HasDeletedPersonalData => PersonalDataDeleted || Status == ResidentRegistrationStatus.Deleted ||
        PersonalDataDeletedAt is not null && Status != ResidentRegistrationStatus.Approved;
}
