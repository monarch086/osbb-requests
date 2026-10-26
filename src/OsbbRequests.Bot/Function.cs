using OsbbRequests.Shared.Telegram;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleSystemsManagement;
using Amazon.SQS;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Bot.Webhook;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace OsbbRequests.Bot;

public sealed class Function
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly WebhookHandler handler;
    private readonly IServiceNotifier? notifier;

    public Function() : this(new WebhookHandler(
        new SsmSecretProvider(new AmazonSimpleSystemsManagementClient(),
            RuntimeConfiguration.Required("WEBHOOK_SECRET_PARAMETER")),
        new CommandQueue(new AmazonSQSClient(), RuntimeConfiguration.Required("COMMAND_QUEUE_URL")),
        RuntimeConfiguration.Required("TELEGRAM_BOT_USERNAME")))
    {
        notifier = TelegramServiceNotifier.FromEnvironment(Http);
    }

    public Function(WebhookHandler handler, IServiceNotifier? notifier = null)
    { this.handler = handler; this.notifier = notifier; }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        try
        {
            return await handler.HandleAsync(request, timeout.Token);
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"operation=telegram_ingress errorType={ex.GetType().Name}");
            await notifier.ReportSafelyAsync("telegram_ingress", ex, context.AwsRequestId, context.Logger.LogLine);
            // Telegram retries: no success acknowledgement before SQS durably accepts the update.
            return WebhookHandler.Response(503);
        }
    }
}
