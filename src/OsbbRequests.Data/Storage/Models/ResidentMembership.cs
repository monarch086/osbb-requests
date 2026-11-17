namespace OsbbRequests.Data.Storage;

public sealed class ResidentMembership
{
    public required string Source { get; set; }
    public required string Key { get; set; }
    public required string ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public bool Active { get; set; }
    public required string Data { get; set; }
}
