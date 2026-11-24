namespace OsbbRequests.Data.Storage;

public sealed class ResidentPhone
{
    public long ResidentId { get; set; }
    public Resident Resident { get; set; } = null!;
    public required string Phone { get; set; }
}
