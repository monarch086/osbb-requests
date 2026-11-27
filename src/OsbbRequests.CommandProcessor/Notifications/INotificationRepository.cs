using OsbbRequests.Services.Notifications;

namespace OsbbRequests.CommandProcessor.Notifications;

public sealed record NotificationWorkItem(string SortKey, ResidentNotification Notification, int Attempts, Dictionary<string, string>? AudienceCursor = null);
public interface INotificationRepository
{
    Task<IReadOnlyList<NotificationWorkItem>> ListDueAsync(DateTimeOffset now, CancellationToken ct);
    Task ExpandAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct);
    Task<bool> CanDeliverAsync(NotificationWorkItem item, CancellationToken ct);
    Task<bool> ClaimAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct);
    Task CompleteAsync(NotificationWorkItem item, string lease, string disposition, DateTimeOffset now, CancellationToken ct);
    Task CompleteDeliveredAsync(NotificationWorkItem item, string lease, DateTimeOffset now, long? messageId, CancellationToken ct) =>
        CompleteAsync(item, lease, "Delivered", now, ct);
    Task PauseAsync(DateTimeOffset until, CancellationToken ct);
    Task RetryAsync(NotificationWorkItem item, string lease, DateTimeOffset dueAt, CancellationToken ct);
}
