namespace OsbbRequests.Shared.Telegram;

public interface ISecretProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}
