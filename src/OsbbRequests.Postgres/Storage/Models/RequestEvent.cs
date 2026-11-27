namespace OsbbRequests.DataImport.Storage;

public sealed class RequestEvent
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public long RequestId { get; set; }
    public required string EventKey { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? Action { get; set; }
    public string? Actor { get; set; }
    public long? ActorId { get; set; }
    public string? Status { get; set; }
    public string? PreviousStatus { get; set; }
    public string? Resolution { get; set; }
    public bool HasResolution { get; set; }
    public string? Target { get; set; }
    public int? Floor { get; set; }
    public bool HasFloor { get; set; }
    public int? Entrance { get; set; }
    public bool HasEntrance { get; set; }
    public RequestEventSnapshot? Snapshot { get; set; }
}
