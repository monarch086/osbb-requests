using OsbbRequests.Shared.Telegram;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleSystemsManagement;
using OsbbRequests.CommandProcessor.Commands;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.Aws.Bot;
using OsbbRequests.CommandProcessor.Telegram;

namespace OsbbRequests.Bot;

public sealed class CommandFunction
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly TelegramCommandProcessor processor;
    private readonly IQueueRetryScheduler? retryScheduler;
    private readonly IServiceNotifier? notifier;

    public CommandFunction()
    {
        var complexId = RuntimeConfiguration.Required("BUILDING_ID");
        var repository = new DynamoCommandRepository(new AmazonDynamoDBClient(),
            RuntimeConfiguration.Required("COMMAND_DATA_TABLE"), residentsTable: RuntimeConfiguration.Required("RESIDENTS_TABLE"),
            complexId: complexId,
            usePhoneIndex: Environment.GetEnvironmentVariable("PHONE_LOOKUP_MODE") switch
            {
                null or "Directory" => false,
                "PhoneIndex" => true,
                _ => throw new InvalidOperationException("Invalid phone lookup mode.")
            });
        var secrets = new SsmSecretProvider(new AmazonSimpleSystemsManagementClient(),
            RuntimeConfiguration.Required("BOT_TOKEN_PARAMETER"));
        var telegram = new TelegramClient(Http, secrets);
        var approvalMode = ResidentApprovalConfiguration.Parse(Environment.GetEnvironmentVariable("RESIDENT_APPROVAL_MODE"));
        var handler = new CommandHandler(repository, RuntimeConfiguration.Required("TELEGRAM_BOT_USERNAME"),
            complexId, residentApprovalMode: approvalMode);
        processor = new TelegramCommandProcessor(repository, handler, telegram, complexId);
        retryScheduler = new QueueRetryScheduler(new Amazon.SQS.AmazonSQSClient(),
            RuntimeConfiguration.Required("COMMAND_QUEUE_URL"));
        notifier = TelegramServiceNotifier.FromEnvironment(Http);
    }

    public CommandFunction(TelegramCommandProcessor processor, IQueueRetryScheduler? retryScheduler = null,
        IServiceNotifier? notifier = null)
    {
        this.processor = processor;
        this.retryScheduler = retryScheduler;
        this.notifier = notifier;
    }

    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent input, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var record in input.Records)
        {
            // For FIFO, stop after the first failure and return unprocessed records too.
            if (failures.Count > 0)
            {
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
                continue;
            }
            try
            {
                var command = JsonSerializer.Deserialize<CommandEnvelope>(record.Body)
                    ?? throw new InvalidOperationException("Invalid queue command.");
                await processor.ProcessAsync(command, timeout.Token);
                if (record.Attributes?.TryGetValue("ApproximateReceiveCount", out var count) == true &&
                    int.TryParse(count, out var receives) && receives > 1)
                    context.Logger.LogLine($"operation=process_command messageId={record.MessageId} outcome=retry_succeeded receiveCount={receives}");
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"operation=process_command messageId={record.MessageId} errorType={ex.GetType().Name}" + TelegramFailureDiagnostics.For(ex));
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
                if (ex is TelegramRateLimitException rateLimit && retryScheduler is not null)
                {
                    try
                    {
                        await retryScheduler.DeferAsync(record.ReceiptHandle, rateLimit.RetryAfterSeconds, timeout.Token);
                    }
                    catch (Exception schedulingError)
                    {
                        context.Logger.LogLine($"operation=defer_command errorType={schedulingError.GetType().Name}");
                        await notifier.ReportSafelyAsync("defer_command", schedulingError, context.AwsRequestId, context.Logger.LogLine);
                    }
                }
                else
                {
                    await notifier.ReportSafelyAsync("process_command", ex, context.AwsRequestId, context.Logger.LogLine);
                }
            }
        }
        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}
