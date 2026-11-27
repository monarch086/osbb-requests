using Microsoft.EntityFrameworkCore;
using OsbbRequests.CommandProcessor.Notifications;
using OsbbRequests.DataImport.Storage;
using NotificationEvent = OsbbRequests.Domain.Models.AdministratorEvent;

namespace OsbbRequests.Postgres.Bot;

public sealed partial class PostgresNotificationRepository(IDbContextFactory<DataContext> factory, PostgresBotScope scope) : INotificationRepository
{
    private const int PageSize = 25;
    private const string PauseKind = "LOCAL_PAUSE";

    public async Task<IReadOnlyList<NotificationWorkItem>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await Rows(db).Where(row => row.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await Rows(db).AnyAsync(row => row.Key == PauseKey && row.DueAt > now, ct)) return [];

        var rows = await Due(Rows(db), now).OrderBy(row => row.DueAt == null ? DateTimeOffset.UnixEpoch : row.DueAt)
            .ThenBy(row => row.Key).Take(PageSize).ToListAsync(ct);
        return rows.Select(row => new NotificationWorkItem(row.Key, row.ToPending(scope.Complex), row.Attempts)).ToArray();
    }

    public async Task<bool> ClaimAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var leasedUntil = now.AddSeconds(90);
        var count = await Due(Rows(db), now).Where(row => row.Key == item.SortKey &&
            !db.Notifications.Any(pause => pause.SourceId == scope.SourceId && pause.Key == PauseKey && pause.DueAt > now))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Lease, lease)
                .SetProperty(row => row.LeaseUntil, leasedUntil), ct);
        return count == 1;
    }

    public async Task CompleteAsync(NotificationWorkItem item, string lease, string disposition, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await CompleteRowsAsync(Claimed(db, item, lease), disposition, now, ct);
    }

    public async Task CompleteDeliveredAsync(NotificationWorkItem item, string lease, DateTimeOffset now, long? messageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var count = await CompleteRowsAsync(Claimed(db, item, lease), "Delivered", now, ct);
        var notification = item.Notification;
        if (count == 1 && messageId is > 0 && notification is { OwnerId: > 0, RequestId: { } requestId, AdministratorId: null })
        {
            db.Notifications.Add(new()
            {
                SourceId = scope.SourceId,
                ComplexId = scope.ComplexId,
                Key = scope.ReplyKey(notification.OwnerId, messageId.Value),
                Kind = "LOCAL_MESSAGE",
                ExpiresAt = now.AddDays(30),
                RequestId = requestId
            });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task RetryAsync(NotificationWorkItem item, string lease, DateTimeOffset dueAt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await Claimed(db, item, lease).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Attempts, row => row.Attempts + 1).SetProperty(row => row.DueAt, dueAt)
            .SetProperty(row => row.Lease, (string?)null).SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null), ct);
    }

    public async Task PauseAsync(DateTimeOffset until, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications ("SourceId", "ComplexId", "Key", "Kind", "DueAt", "Attempts")
            VALUES ({scope.SourceId}, {scope.ComplexId}, {PauseKey}, {PauseKind}, {until}, 0)
            ON CONFLICT ("SourceId", "Key") DO UPDATE
                SET "DueAt" = GREATEST(notifications."DueAt", EXCLUDED."DueAt")
            """, ct);
    }

    private static Task<int> CompleteRowsAsync(IQueryable<Notification> rows, string disposition, DateTimeOffset now, CancellationToken ct)
    {
        var kind = "LOCAL_" + disposition.ToUpperInvariant();
        var expiresAt = now.AddDays(30);
        return rows.ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Kind, kind).SetProperty(row => row.ExpiresAt, expiresAt)
            .SetProperty(row => row.CompletedAt, now).SetProperty(row => row.Disposition, disposition)
            .SetProperty(row => row.OwnerId, (long?)null).SetProperty(row => row.RequestId, (long?)null)
            .SetProperty(row => row.RequestVersion, (int?)null).SetProperty(row => row.Text, (string?)null)
            .SetProperty(row => row.CreatedAt, (DateTimeOffset?)null).SetProperty(row => row.AdministratorEvent, (NotificationEvent?)null)
            .SetProperty(row => row.AdministratorId, (string?)null).SetProperty(row => row.ResidentBroadcastId, (string?)null)
            .SetProperty(row => row.RegistrationId, (string?)null).SetProperty(row => row.AudienceAfter, (string?)null)
            .SetProperty(row => row.Lease, (string?)null).SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null), ct);
    }

    private static IQueryable<Notification> Due(IQueryable<Notification> rows, DateTimeOffset now) =>
        rows.Where(row => row.Kind == Notification.PendingKind && (row.DueAt == null || row.DueAt <= now) &&
            (row.LeaseUntil == null || row.LeaseUntil <= now));

    private IQueryable<Notification> Rows(DataContext db) => db.Notifications.Where(row =>
        row.SourceId == scope.SourceId && row.ComplexId == scope.ComplexId);

    private IQueryable<Notification> Claimed(DataContext db, NotificationWorkItem item, string lease) =>
        Rows(db).Where(row => row.Key == item.SortKey && row.Kind == Notification.PendingKind && row.Lease == lease);

    private string PauseKey => $"LOCAL_PAUSE#{scope.ComplexId}#{scope.Bot}";
}
