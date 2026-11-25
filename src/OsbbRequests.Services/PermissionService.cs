using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public sealed class PermissionService(IAdministratorStore store)
{
    public static void RequirePermissionsEdit(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.IsSuperAdmin) throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для зміни доступів.");
    }

    public async Task SetPermissionsAsync(AdminUser actor, string targetId, PermissionsChange change, CancellationToken ct)
    {
        RequirePermissionsEdit(actor);
        var target = await GetAdminForChangeAsync(actor, targetId, change.Version, ct);
        await store.SetPermissionsAsync(actor, target, change, ct);
    }

    public static void RequireNotificationEdit(AdminUser actor, string targetId)
    {
        AdminAccess.RequireActive(actor);
        if (actor.UserId != targetId && !actor.IsSuperAdmin)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для зміни сповіщень.");
    }

    public async Task SetNotificationSettingsAsync(AdminUser actor, string targetId, NotificationSettingsChange change, CancellationToken ct)
    {
        RequireNotificationEdit(actor, targetId);
        var target = await store.GetAdminAsync(targetId, ct) ?? throw new AdminOperationException(AdminFailure.NotFound, "Користувача не знайдено.");
        RequireVersion(target, change.Version);
        await store.SetNotificationSettingsAsync(actor, target, change, ct);
    }

    public static void RequireAdministratorManagement(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.IsSuperAdmin) throw new AdminOperationException(AdminFailure.Forbidden, "Керувати адміністраторами може лише супер адміністратор.");
    }

    public Task<Page<AdminUser>> ListAdminsAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireAdministratorManagement(actor);
        return store.ListAdminsAsync(cursor, ct);
    }

    public async Task SetAccessAsync(AdminUser actor, string targetId, AccessChange change, CancellationToken ct)
    {
        RequireAdministratorManagement(actor);
        var target = await GetAdminForChangeAsync(actor, targetId, change.Version, ct);
        await store.SetAccessAsync(actor, target, change, ct);
    }

    private async Task<AdminUser> GetAdminForChangeAsync(AdminUser actor, string targetId, int version, CancellationToken ct)
    {
        var target = await store.GetAdminAsync(targetId, ct) ?? throw new AdminOperationException(AdminFailure.NotFound, "Користувача не знайдено.");
        if (target.IsSuperAdmin || target.UserId == actor.UserId)
            throw new AdminOperationException(AdminFailure.Forbidden, "Цей обліковий запис керується через AWS.");
        RequireVersion(target, version);
        return target;
    }

    private static void RequireVersion(AdminUser target, int version)
    {
        if (version != target.Version) throw new AdminOperationException(AdminFailure.Conflict, "Профіль змінено. Оновіть список.");
    }
}
