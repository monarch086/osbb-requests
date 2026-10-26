using OsbbRequests.Shared.Telegram;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.SimpleSystemsManagement;
using OsbbRequests.Bot.Notifications;
using OsbbRequests.Bot.Telegram;

namespace OsbbRequests.Bot;

public sealed class NotificationFunction
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly NotificationProcessor processor;
    private readonly IServiceNotifier? notifier;
    public NotificationFunction()
    {
        var telegram = new TelegramClient(Http, new SsmSecretProvider(new AmazonSimpleSystemsManagementClient(),
            RuntimeConfiguration.Required("BOT_TOKEN_PARAMETER")));
        notifier = TelegramServiceNotifier.FromEnvironment(Http);
        processor = new(new DynamoNotificationRepository(new AmazonDynamoDBClient(),
            RuntimeConfiguration.Required("COMMAND_DATA_TABLE"), RuntimeConfiguration.Required("BUILDING_ID"), RuntimeConfiguration.Required("ADMINISTRATORS_TABLE")), telegram, notifier);
    }
    public NotificationFunction(NotificationProcessor processor, IServiceNotifier? notifier = null)
    { this.processor = processor; this.notifier = notifier; }
    public async Task FunctionHandler(JsonElement input, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try { await processor.RunAsync(timeout.Token, context.Logger.LogLine); }
        catch (Exception error)
        {
            context.Logger.LogLine($"operation=send_notifications errorType={error.GetType().Name}");
            await notifier.ReportSafelyAsync("send_notifications", error, context.AwsRequestId, context.Logger.LogLine);
            throw;
        }
    }
}
