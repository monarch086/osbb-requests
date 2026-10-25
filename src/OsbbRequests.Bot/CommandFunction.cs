using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleSystemsManagement;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Telegram;

namespace OsbbRequests.Bot;

public sealed class CommandFunction
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly CommandProcessor processor;
    private readonly IQueueRetryScheduler? retryScheduler;
    private readonly IServiceNotifier? notifier;

    public CommandFunction()
    {
        var repository = new DynamoCommandRepository(new AmazonDynamoDBClient(),
            RuntimeConfiguration.Required("COMMAND_DATA_TABLE"));
        var secrets = new SsmSecretProvider(new AmazonSimpleSystemsManagementClient(),
            RuntimeConfiguration.Required("BOT_TOKEN_PARAMETER"));
        var telegram = new TelegramClient(Http, secrets);
        processor = new CommandProcessor(repository,
            new CommandHandler(repository, RuntimeConfiguration.Required("TELEGRAM_BOT_USERNAME"),
                RuntimeConfiguration.Required("BUILDING_ID")), telegram, RuntimeConfiguration.Required("BUILDING_ID"));
        retryScheduler = new QueueRetryScheduler(new Amazon.SQS.AmazonSQSClient(),
            RuntimeConfiguration.Required("COMMAND_QUEUE_URL"));
        if (long.TryParse(Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_CHAT_ID"), out var chatId) && chatId != 0)
            notifier = new ServiceNotifier(telegram, chatId);
    }

    public CommandFunction(CommandProcessor processor, IQueueRetryScheduler? retryScheduler = null,
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
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"operation=process_command messageId={record.MessageId} errorType={ex.GetType().Name}");
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
                    }
                }
                else if (notifier is not null)
                {
                    await notifier.NotifyFailureAsync(record.MessageId);
                }
            }
        }
        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}
