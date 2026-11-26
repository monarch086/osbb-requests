namespace OsbbRequests.DataImport.Storage;

public sealed class Notification
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public long ComplexId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? Lease { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public string? AudienceAfter { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public required string Data { get; set; }
}
