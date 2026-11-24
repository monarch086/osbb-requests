namespace OsbbRequests.Data.Storage;

public sealed class Complex
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public required string ExternalId { get; set; }
}
