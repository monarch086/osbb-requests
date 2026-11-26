using System.Runtime.InteropServices;
using Amazon.DynamoDBv2;
using Amazon.SimpleSystemsManagement;
using OsbbRequests.Aws.Bot;
using OsbbRequests.CommandListener;
using OsbbRequests.CommandProcessor.Commands;
using OsbbRequests.CommandProcessor.Telegram;
using OsbbRequests.Shared.Telegram;

using var shutdown = new CancellationTokenSource();
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdown.Cancel();
});
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
IServiceNotifier? notifier = null;
AmazonSimpleSystemsManagementClient? secrets = null;
try
{
    var tokenParameter = Required("BOT_TOKEN_PARAMETER");
    var botUsername = Required("TELEGRAM_BOT_USERNAME").TrimStart('@');
    var complexId = Required("BUILDING_ID");
    var commandTable = Required("COMMAND_DATA_TABLE");
    var residentsTable = Required("RESIDENTS_TABLE");
    var approvalMode = ResidentApprovalConfiguration.Parse(Environment.GetEnvironmentVariable("RESIDENT_APPROVAL_MODE"));

    secrets = new AmazonSimpleSystemsManagementClient();
    if (Environment.GetEnvironmentVariable("SERVICE_BOT_TOKEN_PARAMETER") is { Length: > 0 } serviceParameter)
        notifier = new TelegramServiceNotifier(http, new SsmSecretProvider(secrets, serviceParameter),
            "osbb-requests-command-listener", Environment.GetEnvironmentVariable("STAGE") ?? "local",
            Environment.GetEnvironmentVariable("SERVICE_NOTIFICATION_CHAT_ID") ?? TelegramServiceNotifier.ServiceNotificationChatId,
            sourceLabel: "Сервіс", logLocation: "журнал контейнера");

    var telegram = new TelegramClient(http, new SsmSecretProvider(secrets, tokenParameter));
    var actualUsername = await telegram.GetBotUsernameAsync(shutdown.Token);
    if (!string.Equals(botUsername, actualUsername, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The configured username does not match the bot token.");

    using var dynamo = new AmazonDynamoDBClient();
    var repository = new DynamoCommandRepository(dynamo, commandTable,
        residentsTable: residentsTable, complexId: complexId, usePhoneIndex: true);
    var handler = new CommandHandler(repository, botUsername, complexId, residentApprovalMode: approvalMode);
    var processor = new TelegramCommandProcessor(repository, handler, telegram, complexId);
    var loop = new CommandPollingLoop(telegram, processor.ProcessAsync, botUsername);
    Console.WriteLine("operation=listen_commands outcome=started mode=long_polling");
    await loop.RunAsync(Console.Error.WriteLine, shutdown.Token);
    return 0;
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("operation=listen_commands outcome=stopped " +
        (error is ListenerConfigurationException configuration ? $"missingVariable={configuration.Variable}" : $"errorType={error.GetType().Name}") +
        TelegramFailureDiagnostics.For(error));
    await notifier.ReportSafelyAsync("listen_commands", error, log: Console.Error.WriteLine);
    return 1;
}

finally
{
    secrets?.Dispose();
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new ListenerConfigurationException(name);

file sealed class ListenerConfigurationException(string variable) : Exception("Required listener configuration is missing.")
{
    public string Variable { get; } = variable;
}
