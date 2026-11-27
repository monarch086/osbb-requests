namespace OsbbRequests.CommandProcessor.Telegram;

public interface ISecretProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}
