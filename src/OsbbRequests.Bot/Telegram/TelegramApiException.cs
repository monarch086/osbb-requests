using System.Net.Http;
using System.Net.Sockets;

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

public enum TelegramOperation { SendMessage, SetCommandMenu }

public sealed class TelegramApiException(TelegramFailureCategory category, int? statusCode = null,
    TelegramOperation? operation = null, HttpRequestError? requestError = null, SocketError? socketError = null)
    : InvalidOperationException("Telegram API operation failed.")
{
    public TelegramFailureCategory Category { get; } = category;
    public int? StatusCode { get; } = statusCode;
    public TelegramOperation? Operation { get; } = operation;
    public HttpRequestError? RequestError { get; } = requestError;
    public SocketError? SocketError { get; } = socketError;
}

public static class TelegramFailureDiagnostics
{
    public static string For(Exception error) => error switch
    {
        TelegramApiException api => $" telegramFailure={api.Category}" + (api.StatusCode is { } status ? $" telegramHttpStatus={status}" : "") +
            (api.Operation is { } operation ? $" telegramOperation={operation}" : "") +
            (api.RequestError is { } requestError ? $" httpRequestError={requestError}" : "") +
            (api.SocketError is { } socketError ? $" socketError={socketError}" : ""),
        TelegramRecipientUnavailableException recipient => $" telegramFailure=RecipientUnavailable telegramHttpStatus={recipient.StatusCode}" +
            (recipient.Operation is { } operation ? $" telegramOperation={operation}" : ""),
        TelegramRateLimitException limit => $" telegramFailure=RateLimited telegramHttpStatus=429 retryAfterSeconds={limit.RetryAfterSeconds}" +
            (limit.Operation is { } operation ? $" telegramOperation={operation}" : ""),
        _ => ""
    };
}
