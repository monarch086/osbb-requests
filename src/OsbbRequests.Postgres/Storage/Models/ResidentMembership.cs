namespace OsbbRequests.DataImport.Storage;

public sealed class ResidentMembership
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public long? ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public long? ResidentId { get; set; }
    public bool Active { get; set; }
    public string? RegistrationId { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}
