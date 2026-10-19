namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramRateLimitException(int retryAfterSeconds) : Exception("Telegram rate limit exceeded.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
