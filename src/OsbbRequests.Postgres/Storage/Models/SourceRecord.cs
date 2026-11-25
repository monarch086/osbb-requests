namespace OsbbRequests.Data.Storage;

public sealed class SourceRecord
{
    public long SourceId { get; set; }
    public required string Table { get; set; }
    public required string Key { get; set; }
    public required string Hash { get; set; }
    public required string Data { get; set; }
    public int ProjectionVersion { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
