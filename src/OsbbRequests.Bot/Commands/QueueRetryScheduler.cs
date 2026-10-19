using Amazon.SQS;
using Amazon.SQS.Model;

namespace OsbbRequests.Bot.Commands;

public interface IQueueRetryScheduler
{
    Task DeferAsync(string receiptHandle, int retryAfterSeconds, CancellationToken cancellationToken);
}

public sealed class QueueRetryScheduler(IAmazonSQS client, string queueUrl) : IQueueRetryScheduler
{
    public async Task DeferAsync(string receiptHandle, int retryAfterSeconds, CancellationToken cancellationToken) =>
        await client.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle,
            VisibilityTimeout = (int)Math.Clamp((long)retryAfterSeconds + 5, 180, 43200)
        }, cancellationToken);
}
