namespace OsbbRequests.Data.Storage;

public sealed class ResidentEvent
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string RegistrationId { get; set; }
    public required string EventKey { get; set; }
    public required string Data { get; set; }
}
