using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.CommandProcessor.Notifications;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Notifications;

namespace OsbbRequests.Postgres.Bot;

public sealed partial class PostgresNotificationRepository
{
    public async Task<bool> CanDeliverAsync(NotificationWorkItem item, CancellationToken ct)
    {
        var notification = item.Notification;
        if (notification.ComplexId != scope.Complex || notification.IsBroadcast) return false;

        await using var db = await factory.CreateDbContextAsync(ct);
        if (notification.AdministratorId is { } id && notification.AdministratorEvent is { } kind)
        {
            var settings = await Administrators(db).SingleOrDefaultAsync(row => row.UserId == id, ct);
            return settings is not null && Settings(settings).Includes(kind) && Settings(settings).ChatId == notification.OwnerId;
        }
        if (notification.OwnerId <= 0 || notification.AdministratorEvent is not null || notification.AdministratorId is not null) return false;
        return await Residents(db).AnyAsync(row => row.TelegramUserId == notification.OwnerId &&
            (notification.RegistrationId == null || row.RegistrationId == notification.RegistrationId && row.ResidentType == ResidentType.Owner) &&
            (notification.RegistrationId == null && notification.ResidentBroadcastId == null ||
                (row.ApprovedAt ?? row.UpdatedAt) <= notification.CreatedAt), ct);
    }

    public async Task ExpandAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct)
    {
        var notification = item.Notification;
        if (notification.ComplexId != scope.Complex || !notification.IsBroadcast)
            throw new InvalidOperationException("Invalid notification audience.");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = Claimed(db, item, lease);
        var parent = await claimed.AsNoTracking().SingleOrDefaultAsync(ct);
        if (parent is null) return;

        var (recipients, next) = notification.ResidentBroadcastId is not null
            ? await ResidentPageAsync(db, notification, parent.AudienceAfter, ct)
            : await AdministratorPageAsync(db, notification, parent.AudienceAfter, ct);

        var count = next is null
            ? await CompleteRowsAsync(claimed, "Expanded", now, ct)
            : await claimed.ExecuteUpdateAsync(update => update.SetProperty(row => row.AudienceAfter, next)
                .SetProperty(row => row.Lease, (string?)null).SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(row => row.DueAt, now), ct);
        if (count == 0) return;

        db.Notifications.AddRange(recipients.Select(recipient =>
            Notification.CreatePending(scope.SourceId, scope.ComplexId, scope.Complex, recipient, now)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<(ResidentNotification[] Recipients, string? Next)> ResidentPageAsync(DataContext db,
        ResidentNotification notification, string? cursor, CancellationToken ct)
    {
        var after = cursor is null ? 0L : long.Parse(cursor, CultureInfo.InvariantCulture);
        var ids = await Residents(db).Where(row => row.TelegramUserId > after &&
                (row.ApprovedAt ?? row.UpdatedAt) <= notification.CreatedAt)
            .OrderBy(row => row.TelegramUserId).Select(row => row.TelegramUserId).Take(PageSize + 1).ToListAsync(ct);
        var recipients = ids.Take(PageSize).Select(id => notification with
        {
            Id = FormattableString.Invariant($"{notification.Id}-resident-{id}"),
            OwnerId = id
        }).ToArray();
        return (recipients, ids.Count > PageSize ? ids[PageSize - 1].ToString(CultureInfo.InvariantCulture) : null);
    }

    private async Task<(ResidentNotification[] Recipients, string? Next)> AdministratorPageAsync(DataContext db,
        ResidentNotification notification, string? after, CancellationToken ct)
    {
        var page = await Administrators(db).Where(row => after == null || string.Compare(row.UserId, after) > 0)
            .OrderBy(row => row.UserId).Take(PageSize + 1).ToListAsync(ct);
        var recipients = new List<ResidentNotification>();
        foreach (var row in page.Take(PageSize))
        {
            var settings = Settings(row);
            if (notification.AdministratorEvent is not { } kind || !settings.Includes(kind) || settings.ChatId is not { } chat) continue;
            recipients.Add(notification with { Id = notification.Id + "-admin-" + row.UserId, OwnerId = chat, AdministratorId = row.UserId });
        }
        return (recipients.ToArray(), page.Count > PageSize ? page[PageSize - 1].UserId : null);
    }

    private IQueryable<Resident> Residents(DataContext db) => db.Residents.Where(row => row.ComplexId == scope.ComplexId &&
        row.Stage == RegistrationStage.Approved && !row.PersonalDataDeleted);

    private IQueryable<AdministratorNotificationSetting> Administrators(DataContext db) =>
        from settings in db.AdministratorNotificationSettings
        join admin in db.Administrators on new { settings.SourceId, settings.UserId } equals new { admin.SourceId, admin.UserId }
        where settings.SourceId == scope.SourceId && admin.IsActive
        select settings;

    private static AdministratorNotificationSettings Settings(AdministratorNotificationSetting row) => new(row.TelegramChatId,
        row.NewResidents, row.NewRequests, row.NewAdministrators, row.NewKeyOrders, row.NewApartmentRegistrations);
}
