namespace OsbbRequests.DataImport.Storage;

public sealed class ResidentConsent
{
    public long ResidentId { get; set; }
    public Resident Resident { get; set; } = null!;
    public DateTimeOffset? ConsentedAt { get; set; }
    public string? Version { get; set; }
}
