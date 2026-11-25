using System.Globalization;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Webhook;

public interface ICommandQueue
{
    Task EnqueueAsync(CommandEnvelope command, CancellationToken cancellationToken);
}

public sealed class CommandQueue(IAmazonSQS client, string queueUrl) : ICommandQueue
{
    public async Task EnqueueAsync(CommandEnvelope command, CancellationToken cancellationToken) =>
        await client.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(command),
            MessageGroupId = command.ChatId.ToString(CultureInfo.InvariantCulture),
            MessageDeduplicationId = command.UpdateId.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);
}
