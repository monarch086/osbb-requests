using OsbbRequests.Shared.Telegram;

namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramRateLimitException(int retryAfterSeconds, TelegramOperation? operation = null, TelegramErrorResponse? response = null) : Exception("Telegram rate limit exceeded."), ITelegramFailureDetails
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
    public TelegramOperation? Operation { get; } = operation;
    public TelegramErrorResponse? Response { get; } = response;
    public string SafeDiagnostics => TelegramFailureDiagnostics.For(this);
}
