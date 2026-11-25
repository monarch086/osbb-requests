namespace OsbbRequests.DataImport.Storage;

public sealed class Administrator
{
    public long SourceId { get; set; }
    public required string UserId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
