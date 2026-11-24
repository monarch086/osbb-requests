namespace OsbbRequests.Data.Storage;

public sealed class Notification
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string ComplexId { get; set; }
    public required string Kind { get; set; }
    public required string Data { get; set; }
}
