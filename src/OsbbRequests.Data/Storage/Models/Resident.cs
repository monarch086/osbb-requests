namespace OsbbRequests.Data.Storage;

public sealed class Resident
{
    public required string Source { get; set; }
    public required string ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public required string Payload { get; set; }
}
