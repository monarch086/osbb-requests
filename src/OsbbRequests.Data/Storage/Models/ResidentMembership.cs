namespace OsbbRequests.Data.Storage;

public sealed class ResidentMembership
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public bool Active { get; set; }
    public string? RegistrationId { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}
