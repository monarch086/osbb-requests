namespace OsbbRequests.Data.Storage;

public sealed class RequestKeyItem
{
    public required string Source { get; set; }
    public long OwnerId { get; set; }
    public long RequestId { get; set; }
    public int Position { get; set; }
    public required string Type { get; set; }
    public required string Phone { get; set; }
    public string? SerialNumber { get; set; }
}
