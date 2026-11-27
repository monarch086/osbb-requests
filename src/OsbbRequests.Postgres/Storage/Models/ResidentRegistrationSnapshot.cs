namespace OsbbRequests.DataImport.Storage;

public sealed class ResidentRegistrationSnapshot
{
    public long SourceId { get; set; }
    public long ComplexId { get; set; }
    public required string RegistrationId { get; set; }
    public int Version { get; set; }
    public long TelegramUserId { get; set; }
    public required string Stage { get; set; }
    public string? ResidentType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset? ConsentAt { get; set; }
    public string? ConsentVersion { get; set; }
    public string? ResumeAfterConsent { get; set; }
    public string? BuildingNumber { get; set; }
    public string? ApartmentNumber { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool? PersonalDataDeleted { get; set; }
    public DateTimeOffset? PersonalDataDeletedAt { get; set; }
    public bool? TypeBeforeAddress { get; set; }
    public int? TypeChangeCount { get; set; }
}
