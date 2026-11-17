namespace OsbbRequests.Data.Storage;

public sealed class AdministratorEvent
{
    public required string Source { get; set; }
    public required string Key { get; set; }
    public required string AdministratorId { get; set; }
    public required string Data { get; set; }
}
