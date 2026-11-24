using OsbbRequests.Shared.Models;

namespace OsbbRequests.Data.Storage;

public sealed class Resident
{
    public long Id { get; set; }
    public long ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public string? RegistrationId { get; set; }
    public RegistrationStage Stage { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DecisionReason { get; set; }
    public string? BuildingNumber { get; set; }
    public string? ApartmentNumber { get; set; }
    public bool PersonalDataDeleted { get; set; }
    public DateTimeOffset? PersonalDataDeletedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? Email { get; set; }
    public RegistrationStage? ResumeAfterConsent { get; set; }
    public ResidentType ResidentType { get; set; }
    public bool TypeBeforeAddress { get; set; }
    public int TypeChangeCount { get; set; }
}
