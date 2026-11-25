namespace OsbbRequests.DataImport.Storage;

public sealed class ImportRun
{
    public Guid Id { get; set; }
    public long SourceId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public required string Status { get; set; }
    public long ReadCount { get; set; }
    public long ChangedCount { get; set; }
}
