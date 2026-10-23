using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Notifications;

public sealed record NotificationWorkItem(string SortKey, ResidentNotification Notification, int Attempts);
public interface INotificationRepository
{
    Task<IReadOnlyList<NotificationWorkItem>> ListDueAsync(DateTimeOffset now, CancellationToken ct);
    Task<bool> ClaimAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct);
    Task CompleteAsync(NotificationWorkItem item, string lease, string disposition, DateTimeOffset now, CancellationToken ct);
    Task PauseAsync(DateTimeOffset until, CancellationToken ct);
    Task RetryAsync(NotificationWorkItem item, string lease, DateTimeOffset dueAt, CancellationToken ct);
}
