namespace OsbbRequests.Bot.Telegram;

public interface ITelegramClient
{
    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken);
}
