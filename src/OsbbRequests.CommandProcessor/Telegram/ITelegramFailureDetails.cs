namespace OsbbRequests.CommandProcessor.Telegram;

/// <summary>Controlled diagnostic fields suitable for operational logs and alerts.</summary>
public interface ITelegramFailureDetails
{
    string SafeDiagnostics { get; }
}
