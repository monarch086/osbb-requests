namespace OsbbRequests.DataImport.Storage;

public sealed class BotConversation
{
    public long ComplexId { get; set; }
    public required string Bot { get; set; }
    public long UserId { get; set; }
    public required string Kind { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public required string Data { get; set; }
}
