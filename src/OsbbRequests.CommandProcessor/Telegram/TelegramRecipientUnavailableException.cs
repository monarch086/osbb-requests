namespace OsbbRequests.CommandProcessor.Telegram;

public sealed class TelegramRecipientUnavailableException(int statusCode, TelegramOperation? operation = null, TelegramErrorResponse? response = null)
    : InvalidOperationException($"Telegram recipient unavailable (HTTP {statusCode})."), ITelegramFailureDetails
{
    public int StatusCode { get; } = statusCode;
    public TelegramOperation? Operation { get; } = operation;
    public TelegramErrorResponse? Response { get; } = response;
    public string SafeDiagnostics => TelegramFailureDiagnostics.For(this);
}
