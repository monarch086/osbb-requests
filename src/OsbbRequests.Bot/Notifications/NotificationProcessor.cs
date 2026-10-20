using OsbbRequests.Bot.Telegram;

namespace OsbbRequests.Bot.Notifications;

public sealed class NotificationProcessor(INotificationRepository repository, ITelegramClient telegram,
    IServiceNotifier? notifier = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task RunAsync(CancellationToken ct, Action<string>? log = null)
    {
        var items = await repository.ListDueAsync(clock.GetUtcNow(), ct);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var lease = Guid.NewGuid().ToString("N");
            if (!await repository.ClaimAsync(item, lease, clock.GetUtcNow(), ct)) continue;
            try
            {
                await telegram.SendMessageAsync(item.Notification.OwnerId, item.Notification.Text, ct);
                await repository.CompleteAsync(item, lease, "Delivered", clock.GetUtcNow(), ct);
                log?.Invoke($"operation=deliver_notification notificationId={item.Notification.Id} outcome=delivered");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TelegramRecipientUnavailableException)
            {
                await repository.CompleteAsync(item, lease, "Undeliverable", clock.GetUtcNow(), ct);
                log?.Invoke($"operation=deliver_notification notificationId={item.Notification.Id} outcome=undeliverable");
                if (notifier is not null) await notifier.NotifyFailureAsync(item.Notification.Id);
            }
            catch (Exception error)
            {
                log?.Invoke($"operation=deliver_notification notificationId={item.Notification.Id} outcome=retry errorType={error.GetType().Name}");
                var delay = error is TelegramRateLimitException rate ? Math.Max(1, rate.RetryAfterSeconds) :
                    (int)Math.Min(3600, 60 * Math.Pow(2, Math.Min(item.Attempts, 6)));
                var dueAt = clock.GetUtcNow().AddSeconds(delay);
                if (error is TelegramRateLimitException) await repository.PauseAsync(dueAt, ct);
                await repository.RetryAsync(item, lease, dueAt, ct);
                if (error is TelegramRateLimitException) break; // Respect a bot-wide retry_after too.
                if (notifier is not null) await notifier.NotifyFailureAsync(item.Notification.Id);
            }
        }
    }
}
