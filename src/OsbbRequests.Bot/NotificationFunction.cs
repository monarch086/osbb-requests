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
        if (long.TryParse(Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_CHAT_ID"), out var chatId) && chatId != 0)
            notifier = new ServiceNotifier(telegram, chatId);
        processor = new(new DynamoNotificationRepository(new AmazonDynamoDBClient(),
            RuntimeConfiguration.Required("COMMAND_DATA_TABLE"), RuntimeConfiguration.Required("BUILDING_ID")), telegram, notifier);
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
            if (notifier is not null) await notifier.NotifyFailureAsync("notification-worker");
            throw;
        }
    }
}
