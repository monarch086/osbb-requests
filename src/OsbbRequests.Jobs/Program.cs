using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OsbbRequests.CommandProcessor.Notifications;
using OsbbRequests.CommandProcessor.Telegram;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Jobs;
using OsbbRequests.Postgres.Bot;
using OsbbRequests.Shared.Telegram;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;
var connection = Required("POSTGRES_CONNECTION_STRING");
var username = Required("TELEGRAM_BOT_USERNAME").TrimStart('@');
var token = Required("TELEGRAM_BOT_TOKEN");
var configuredId = configuration.GetValue<long?>("POSTGRES_COMPLEX_ID");

var database = new PooledDbContextFactory<DataContext>(new DbContextOptionsBuilder<DataContext>().UseNpgsql(connection).Options);
using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var scope = await PostgresBotScope.LoadAsync(database, Required("BUILDING_ID"), username, startup.Token, configuredId);
var queue = $"notifications_{scope.ComplexId}_{scope.Bot}";
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var telegram = new TelegramClient(http, new LocalSecret(token));
if (!string.Equals(await telegram.GetBotUsernameAsync(startup.Token), username, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The configured username does not match the bot token.");

IServiceNotifier? notifier = null;
if (configuration["SERVICE_BOT_TOKEN"] is { Length: > 0 } serviceToken)
{
    notifier = new TelegramServiceNotifier(http, new LocalSecret(serviceToken), "osbb-requests-jobs",
        configuration["STAGE"] ?? "local", configuration["SERVICE_NOTIFICATION_CHAT_ID"] ?? TelegramServiceNotifier.ServiceNotificationChatId);
}

builder.Services.AddSingleton(new NotificationProcessor(new PostgresNotificationRepository(database, scope), telegram, notifier));
builder.Services.AddSingleton(new NotificationJobDiagnostics(notifier));
builder.Services.AddHangfire(config => config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(storage => storage.UseNpgsqlConnection(connection)));
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 1;
    options.Queues = [queue];
});
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);

using var host = builder.Build();
host.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<NotificationDeliveryJob>(
    $"telegram-notifications-{scope.ComplexId}-{scope.Bot}", queue,
    job => job.RunAsync(CancellationToken.None), Cron.Minutely());
await host.RunAsync();

string Required(string key) => configuration[key] is { Length: > 0 } value ? value :
    throw new InvalidOperationException($"Missing configuration: {key}.");

file sealed class LocalSecret(string token) : ISecretProvider
{
    public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(token);
}
