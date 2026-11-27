using System.Net.Sockets;
using OsbbRequests.Shared.Telegram;

namespace OsbbRequests.CommandProcessor.Telegram;

public enum TelegramFailureCategory
{
    HttpError,
    Transport,
    Timeout,
    InvalidResponse,
    ApiRejected,
    UnconfirmedResult
}

public enum TelegramOperation { SendMessage, SetCommandMenu, GetUpdates, GetMe }

public sealed class TelegramApiException(TelegramFailureCategory category, int? statusCode = null,
    TelegramOperation? operation = null, HttpRequestError? requestError = null, SocketError? socketError = null, TelegramErrorResponse? response = null)
    : InvalidOperationException("Telegram API operation failed."), ITelegramFailureDetails
{
    public TelegramFailureCategory Category { get; } = category;
    public int? StatusCode { get; } = statusCode;
    public TelegramOperation? Operation { get; } = operation;
    public TelegramErrorResponse? Response { get; } = response;
    public string SafeDiagnostics => TelegramFailureDiagnostics.For(this);
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
            (api.SocketError is { } socketError ? $" socketError={socketError}" : "") + ResponseFields(api.Response),
        TelegramRecipientUnavailableException recipient => $" telegramFailure=RecipientUnavailable telegramHttpStatus={recipient.StatusCode}" +
            (recipient.Operation is { } operation ? $" telegramOperation={operation}" : "") + ResponseFields(recipient.Response),
        TelegramRateLimitException limit => $" telegramFailure=RateLimited telegramHttpStatus=429 retryAfterSeconds={limit.RetryAfterSeconds}" +
            (limit.Operation is { } operation ? $" telegramOperation={operation}" : "") + ResponseFields(limit.Response),
        _ => ""
    };

    private static string ResponseFields(TelegramErrorResponse? response)
    {
        if (response is null) return "";
        return $" telegramReason={response.Reason}" +
            (response.Ok is { } ok ? $" telegramOk={(ok ? "true" : "false")}" : "") +
            (response.ErrorCode is { } code ? $" telegramErrorCode={code}" : "") +
            (response.SafeDescription is { } description ? $" telegramDescription=\"{description}\"" : "");
    }
}
