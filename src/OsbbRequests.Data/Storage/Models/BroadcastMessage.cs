namespace OsbbRequests.Data.Storage;

public sealed class BroadcastMessage
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string ComplexId { get; set; }
    public required string MessageId { get; set; }
    public required string Text { get; set; }
    public required string CreatedById { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
