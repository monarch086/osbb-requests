namespace OsbbRequests.DataImport.Storage;

public sealed class BotCommandReceipt
{
    public long ComplexId { get; set; }
    public required string Bot { get; set; }
    public long UpdateId { get; set; }
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public required string Text { get; set; }
    public int? Keyboard { get; set; }
    public bool Delivered { get; set; }
    public bool RefreshMenu { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
