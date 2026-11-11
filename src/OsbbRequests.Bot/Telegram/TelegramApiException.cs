namespace OsbbRequests.Bot.Telegram;

public enum TelegramFailureCategory
{
    HttpError,
    Transport,
    Timeout,
    InvalidResponse,
    ApiRejected,
    UnconfirmedResult
}

public sealed class TelegramApiException(TelegramFailureCategory category, int? statusCode = null)
    : InvalidOperationException("Telegram API operation failed.")
{
    public TelegramFailureCategory Category { get; } = category;
    public int? StatusCode { get; } = statusCode;
}

public static class TelegramFailureDiagnostics
{
    public static string For(Exception error) => error switch
    {
        TelegramApiException api => $" telegramFailure={api.Category}" + (api.StatusCode is { } status ? $" telegramHttpStatus={status}" : ""),
        TelegramRecipientUnavailableException recipient => $" telegramFailure=RecipientUnavailable telegramHttpStatus={recipient.StatusCode}",
        TelegramRateLimitException => " telegramFailure=RateLimited telegramHttpStatus=429",
        _ => ""
    };
}
