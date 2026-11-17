namespace OsbbRequests.Data.Storage;

public sealed class ResidentConsent
{
    public required string Source { get; set; }
    public required string ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public DateTimeOffset ConsentedAt { get; set; }
    public string? Version { get; set; }
}
