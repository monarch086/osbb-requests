namespace OsbbRequests.Data.Storage;

public sealed class RequestEvent
{
    public required string Source { get; set; }
    public required string Key { get; set; }
    public long RequestId { get; set; }
    public required string EventKey { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public required string Data { get; set; }
}
