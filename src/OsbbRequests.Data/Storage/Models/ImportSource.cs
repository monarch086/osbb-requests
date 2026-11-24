namespace OsbbRequests.Data.Storage;

public sealed class ImportSource
{
    public long Id { get; set; }
    public string? AwsAccountId { get; set; }
    public required string Region { get; set; }
    public required string Stage { get; set; }
}
