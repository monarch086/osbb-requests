using Hangfire;
using Microsoft.Extensions.Logging;
using OsbbRequests.CommandProcessor.Notifications;
using OsbbRequests.Shared.Telegram;

namespace OsbbRequests.Jobs;

public sealed record NotificationJobDiagnostics(IServiceNotifier? Notifier);

public sealed class NotificationDeliveryJob(NotificationProcessor processor, NotificationJobDiagnostics diagnostics,
    ILogger<NotificationDeliveryJob> logger)
{
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(30)]
    public async Task RunAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        void Log(string message) => logger.LogInformation("{NotificationEvent}", message);
        try
        {
            await processor.RunAsync(timeout.Token, Log);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Notification run reached its time limit.");
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            await diagnostics.Notifier.ReportSafelyAsync("send_notifications", error, log: Log);
            // Hangfire persists failures. Keep that record free of connection strings and payloads.
            throw new InvalidOperationException($"Notification job failed ({error.GetType().Name}).");
        }
    }
}
