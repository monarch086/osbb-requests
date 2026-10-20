namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramRecipientUnavailableException(int statusCode)
    : InvalidOperationException($"Telegram recipient unavailable (HTTP {statusCode}).");
