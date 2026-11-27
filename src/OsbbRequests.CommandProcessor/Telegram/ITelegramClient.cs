using OsbbRequests.CommandProcessor.Models;

namespace OsbbRequests.CommandProcessor.Telegram;

public interface ITelegramClient
{
    Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken, bool force = false);
    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null);
    async Task<long?> SendNotificationAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        await SendMessageAsync(chatId, text, cancellationToken);
        return null;
    }
}
