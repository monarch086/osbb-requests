using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Postgres.Bot;
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
try
{
    var db = new PooledDbContextFactory<DataContext>(new DbContextOptionsBuilder<DataContext>()
        .UseNpgsql(Required("POSTGRES_CONNECTION_STRING")).Options);
    if (args is ["migrate"])
    {
        await using var context = await db.CreateDbContextAsync(shutdown.Token);
        await context.Database.MigrateAsync(shutdown.Token);
        Console.WriteLine("operation=migrate_postgres outcome=completed");
        return 0;
    }

    var token = Required("TELEGRAM_BOT_TOKEN");
    var botUsername = Required("TELEGRAM_BOT_USERNAME").TrimStart('@');
    var complexId = Required("BUILDING_ID");
    var approvalMode = ResidentApprovalConfiguration.Parse(Environment.GetEnvironmentVariable("RESIDENT_APPROVAL_MODE"));
    var repository = await PostgresCommandRepository.ConnectAsync(db, complexId, botUsername, shutdown.Token, PostgresComplexId());

    if (Environment.GetEnvironmentVariable("SERVICE_BOT_TOKEN") is { Length: > 0 } serviceToken)
        notifier = new TelegramServiceNotifier(http, new LocalSecret(serviceToken),
            "osbb-requests-command-listener", Environment.GetEnvironmentVariable("STAGE") ?? "local",
            Environment.GetEnvironmentVariable("SERVICE_NOTIFICATION_CHAT_ID") ?? TelegramServiceNotifier.ServiceNotificationChatId,
            sourceLabel: "Сервіс", logLocation: "журнал контейнера");

    var telegram = new TelegramClient(http, new LocalSecret(token));
    var actualUsername = await telegram.GetBotUsernameAsync(shutdown.Token);
    if (!string.Equals(botUsername, actualUsername, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The configured username does not match the bot token.");

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

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new ListenerConfigurationException(name);

static long? PostgresComplexId()
{
    var value = Environment.GetEnvironmentVariable("POSTGRES_COMPLEX_ID");
    if (string.IsNullOrEmpty(value)) return null;
    if (long.TryParse(value, out var id) && id > 0) return id;
    throw new ListenerConfigurationException("POSTGRES_COMPLEX_ID");
}

file sealed class LocalSecret(string value) : ISecretProvider
{
    public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(value);
}

file sealed class ListenerConfigurationException(string variable) : Exception("Required listener configuration is missing.")
{
    public string Variable { get; } = variable;
}
