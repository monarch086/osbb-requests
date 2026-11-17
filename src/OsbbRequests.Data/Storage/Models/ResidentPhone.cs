namespace OsbbRequests.Data.Storage;

public sealed class ResidentPhone
{
    public required string Source { get; set; }
    public required string ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public required string Phone { get; set; }
}
