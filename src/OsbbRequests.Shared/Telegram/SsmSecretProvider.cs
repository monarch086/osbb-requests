using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace OsbbRequests.Shared.Telegram;

public interface ISecretProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}

public sealed class SsmSecretProvider(IAmazonSimpleSystemsManagement client, string parameterName) : ISecretProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;
    private DateTimeOffset expiresAt;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && DateTimeOffset.UtcNow < expiresAt)
                return cached;
            var response = await client.GetParameterAsync(new GetParameterRequest
            {
                Name = parameterName,
                WithDecryption = true
            }, cancellationToken);
            if (string.IsNullOrWhiteSpace(response.Parameter?.Value))
                throw new InvalidOperationException("Secret is not configured.");
            cached = response.Parameter.Value;
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}
