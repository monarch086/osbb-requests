using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Telegram;

public interface ITelegramClient
{
    Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken);
    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null);
}
