namespace OsbbRequests.Data.Storage;

public sealed class Request
{
    public required string Source { get; set; }
    public long OwnerId { get; set; }
    public long RequestId { get; set; }
    public required string ComplexId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Payload { get; set; }
}
