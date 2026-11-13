namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramRateLimitException(int retryAfterSeconds, TelegramOperation? operation = null) : Exception("Telegram rate limit exceeded.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
    public TelegramOperation? Operation { get; } = operation;
}
