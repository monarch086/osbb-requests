using Amazon.SimpleSystemsManagement;

namespace OsbbRequests.Shared.Telegram;

public static class AwsTelegramServiceNotifier
{
    public static TelegramServiceNotifier FromEnvironment(HttpClient http) => new(http,
        new SsmSecretProvider(new AmazonSimpleSystemsManagementClient(),
            Environment.GetEnvironmentVariable("SERVICE_BOT_TOKEN_PARAMETER") ?? "/shared/telegram/service-bot-token"),
        Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME") ?? "unknown",
        Environment.GetEnvironmentVariable("STAGE") ?? "unknown",
        Environment.GetEnvironmentVariable("SERVICE_NOTIFICATION_CHAT_ID") ?? TelegramServiceNotifier.ServiceNotificationChatId);
}
