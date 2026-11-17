namespace OsbbRequests.Data.Storage;

public sealed class Notification
{
    public required string Source { get; set; }
    public required string Key { get; set; }
    public required string ComplexId { get; set; }
    public required string Kind { get; set; }
    public required string Data { get; set; }
}
