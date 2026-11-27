using Microsoft.EntityFrameworkCore;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;
using StoredAdministratorEvent = OsbbRequests.DataImport.Storage.AdministratorEvent;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct) =>
        Read((db, currentScope) => ReadAdminAsync(db, currentScope, userId, ct), ct);

    private static async Task<AdminUser?> ReadAdminAsync(DataContext db, Scope currentScope, string userId, CancellationToken ct)
    {
        var admin = await db.Administrators.AsNoTracking().SingleOrDefaultAsync(row => row.SourceId == currentScope.SourceId && row.UserId == userId, ct);
        if (admin is null) return null;
        var privileges = await db.AdministratorPermissions.FindAsync([currentScope.SourceId, userId], ct);
        var settings = await db.AdministratorNotificationSettings.FindAsync([currentScope.SourceId, userId], ct);
        var permissions = privileges is null ? PagePermissions.Legacy :
            new PagePermissions(privileges.ComplexRequests, privileges.KeyOrders, privileges.RegisteredResidents, privileges.PendingResidents, privileges.Messages);
        return new(userId, admin.Email ?? "", admin.DisplayName ?? admin.Email ?? "", admin.IsActive, admin.IsSuperAdmin, admin.Version,
            settings?.TelegramChatId, settings?.NewResidents == true, settings?.NewRequests == true, settings?.NewAdministrators == true,
            permissions, settings?.NewKeyOrders == true, settings?.NewApartmentRegistrations == true);
    }

    public async Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct)
    {
        try
        {
            return await Write(async (db, current) =>
            {
                if (await ReadAdminAsync(db, current, identity.UserId, ct) is { } existing) return existing;
                var now = DateTimeOffset.UtcNow;
                db.Administrators.Add(new Administrator
                {
                    SourceId = current.SourceId,
                    UserId = identity.UserId,
                    Email = identity.Email,
                    DisplayName = identity.DisplayName,
                    Version = 1,
                    CreatedAt = now
                });
                Enqueue(db, current, AdministratorNotifications.NewAdministrator(identity.UserId, identity.Email, options.Complex, now));
                return new AdminUser(identity.UserId, identity.Email, identity.DisplayName, false, false, 1, Permissions: PagePermissions.Legacy);
            }, ct);
        }
        catch (AdminOperationException error) when (error.Failure == AdminFailure.Conflict)
        {
            if (await GetAdminAsync(identity.UserId, ct) is { } existing) return existing;
            throw;
        }
    }

    public Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct) => Read(async (db, current) =>
    {
        var offset = Offset(cursor);
        var ids = await db.Administrators.Where(a => a.SourceId == current.SourceId).OrderBy(a => a.UserId)
            .Select(a => a.UserId).Skip(offset).Take(PageSize + 1).ToListAsync(ct);
        var rows = new List<AdminUser>();
        foreach (var id in ids.Take(PageSize))
            if (await ReadAdminAsync(db, current, id, ct) is { } admin) rows.Add(admin);
        return new Page<AdminUser>(rows, NextCursor(offset, ids.Count));
    }, ct);

    public Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct) => Write(async (db, current) =>
    {
        PermissionService.RequireAdministratorManagement(actor);
        var row = await TargetForWriteAsync(db, current, actor, target.UserId, change.Version, allowManagedAccount: false, ct);
        row.IsActive = change.IsActive;
        AuditAdmin(db, current, row, actor, change.IsActive ? "AdminAccessGranted" : "AdminAccessRevoked");
    }, ct);

    public Task SetPermissionsAsync(AdminUser actor, AdminUser target, PermissionsChange change, CancellationToken ct) => Write(async (db, current) =>
    {
        PermissionService.RequirePermissionsEdit(actor);
        if (change.Permissions is not { } permissions) throw Error(AdminFailure.InvalidInput, "Вкажіть доступи.");
        var row = await TargetForWriteAsync(db, current, actor, target.UserId, change.Version, allowManagedAccount: false, ct);
        var settings = await db.AdministratorPermissions.FindAsync([current.SourceId, row.UserId], ct);
        if (settings is null)
        {
            settings = new() { SourceId = current.SourceId, UserId = row.UserId };
            db.AdministratorPermissions.Add(settings);
        }
        settings.ComplexRequests = permissions.ComplexRequests;
        settings.KeyOrders = permissions.KeyOrders;
        settings.RegisteredResidents = permissions.RegisteredResidents;
        settings.PendingResidents = permissions.PendingResidents;
        settings.Messages = permissions.Messages;
        AuditAdmin(db, current, row, actor, "AdminPermissionsChanged", permissions);
    }, ct);

    public Task SetNotificationSettingsAsync(AdminUser actor, AdminUser target, NotificationSettingsChange change, CancellationToken ct) => Write(async (db, current) =>
    {
        PermissionService.RequireNotificationEdit(actor, target.UserId);
        var row = await TargetForWriteAsync(db, current, actor, target.UserId, change.Version, allowManagedAccount: true, ct);
        var keyOrders = change.NotifyNewKeyOrders ?? target.NotifyNewKeyOrders;
        var apartments = change.NotifyNewApartmentRegistrations ?? target.NotifyNewApartmentRegistrations;
        var value = new AdministratorNotificationSettings(change.TelegramChatId?.Trim(), change.NotifyNewResidents,
            change.NotifyNewRequests, change.NotifyNewAdministrators, keyOrders, apartments);
        if (!string.IsNullOrEmpty(value.TelegramChatId) && (value.TelegramChatId.Length > 20 || value.ChatId is null))
            throw Error(AdminFailure.InvalidInput, "Вкажіть коректний Telegram Chat Id.");
        if (value.ChatId is null && (change.NotifyNewResidents || change.NotifyNewRequests || change.NotifyNewAdministrators || keyOrders || apartments))
            throw Error(AdminFailure.InvalidInput, "Для сповіщень вкажіть Telegram Chat Id.");
        var settings = await db.AdministratorNotificationSettings.FindAsync([current.SourceId, row.UserId], ct);
        if (settings is null)
        {
            settings = new() { SourceId = current.SourceId, UserId = row.UserId };
            db.AdministratorNotificationSettings.Add(settings);
        }
        settings.TelegramChatId = value.ChatId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings.NewResidents = change.NotifyNewResidents;
        settings.NewRequests = change.NotifyNewRequests;
        settings.NewAdministrators = change.NotifyNewAdministrators;
        settings.NewKeyOrders = keyOrders;
        settings.NewApartmentRegistrations = apartments;
        AuditAdmin(db, current, row, actor, "AdminNotificationSettingsChanged");
    }, ct);

    private async Task<Administrator> TargetForWriteAsync(DataContext db, Scope current, AdminUser actor, string userId,
        int version, bool allowManagedAccount, CancellationToken ct)
    {
        await CheckActor(db, current, actor, ct);
        var target = await ReadAdminAsync(db, current, userId, ct) ?? throw Error(AdminFailure.NotFound, "Користувача не знайдено.");
        if (!allowManagedAccount && (target.IsSuperAdmin || userId == actor.UserId))
            throw Error(AdminFailure.Forbidden, "Цей обліковий запис керується через AWS.");
        if (target.Version != version) throw Error(AdminFailure.Conflict, "Профіль змінено. Оновіть список.");
        return (await db.Administrators.FindAsync([current.SourceId, userId], ct))!;
    }

    private static void AuditAdmin(DataContext db, Scope current, Administrator target, AdminUser actor, string action, PagePermissions? permissions = null)
    {
        target.UpdatedAt = DateTimeOffset.UtcNow;
        target.Version++;
        var audit = new StoredAdministratorEvent
        {
            SourceId = current.SourceId,
            AdministratorId = target.UserId,
            Key = EventKey(),
            Action = action,
            Actor = actor.Email,
            CreatedAt = target.UpdatedAt
        };
        audit.SetPermissionsSnapshot(permissions);
        db.AdministratorEvents.Add(audit);
    }
}
