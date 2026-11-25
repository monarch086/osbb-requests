namespace OsbbRequests.Data.Storage;

public sealed class ResidentEvent
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string RegistrationId { get; set; }
    public required string EventKey { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public long? KeyRequestId { get; set; }
    public string? PreviousKeyType { get; set; }
    public string? PreviousKeySerialNumber { get; set; }
    public string? CurrentKeyType { get; set; }
    public string? CurrentKeySerialNumber { get; set; }
    public required string Data { get; set; }
}
