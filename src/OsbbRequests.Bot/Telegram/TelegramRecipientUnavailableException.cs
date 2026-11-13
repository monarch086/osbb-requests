namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramRecipientUnavailableException(int statusCode, TelegramOperation? operation = null)
    : InvalidOperationException($"Telegram recipient unavailable (HTTP {statusCode}).")
{
    public int StatusCode { get; } = statusCode;
    public TelegramOperation? Operation { get; } = operation;
}
