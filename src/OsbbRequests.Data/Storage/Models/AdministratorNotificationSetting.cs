namespace OsbbRequests.Data.Storage;

public sealed class AdministratorNotificationSetting
{
    public required string Source { get; set; }
    public required string UserId { get; set; }
    public string? TelegramChatId { get; set; }
    public bool NewResidents { get; set; }
    public bool NewRequests { get; set; }
    public bool NewKeyOrders { get; set; }
    public bool NewAdministrators { get; set; }
}
