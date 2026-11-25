using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public sealed partial class AdminService(IAdminStore store, IIdentityProvider identityProvider, IStatisticsStore? statistics = null)
{
    public async Task<AdminUser> GetOrRegisterAdminAsync(string userId, string? bearerToken, CancellationToken ct)
    {
        var actor = await store.GetAdminAsync(userId, ct);
        if (actor is not null) return actor;
        if (bearerToken is null) throw new AdminOperationException(AdminFailure.Unauthenticated, "Потрібно увійти.");
        var identity = await identityProvider.GetAsync(bearerToken, userId, ct);
        return await store.RegisterAccountAsync(identity, ct);
    }

    public async Task<AdminUser> GetActiveAdminAsync(string userId, CancellationToken ct)
    {
        var actor = await store.GetAdminAsync(userId, ct);
        return RequireActive(actor);
    }

    public async Task<StatisticsReport> GetStatisticsAsync(AdminUser actor, string? period, CancellationToken ct)
    {
        RequireActive(actor);
        if (!actor.IsSuperAdmin) throw new AdminOperationException(AdminFailure.Forbidden, "Статистика доступна лише супер адміністратору.");
        if (!int.TryParse(period ?? "7", out var days) || days is not (1 or 7 or 30))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Оберіть період: день, тиждень або місяць.");
        if (statistics is null) throw new AdminOperationException(AdminFailure.Unavailable, "Статистика тимчасово недоступна.");
        return new(await statistics.ReadAsync(days, ct), days);
    }

    public static void RequireMessagesAccess(AdminUser actor)
    {
        RequireActive(actor);
        if (!actor.EffectivePermissions.Messages)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для роботи з повідомленнями.");
    }

    public Task<Page<BroadcastMessage>> ListMessagesAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireMessagesAccess(actor);
        return store.ListMessagesAsync(cursor, ct);
    }

    public Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct)
    {
        RequireMessagesAccess(actor);
        return store.SendMessageAsync(actor, change, ct);
    }

    public static void RequirePermissionsEdit(AdminUser actor)
    {
        RequireActive(actor);
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
        RequireActive(actor);
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
        RequireActive(actor);
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

    public async Task<Page<Assignee>> ListAssigneesAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireActive(actor);
        if (!actor.EffectivePermissions.ComplexRequests && !actor.EffectivePermissions.KeyOrders)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для роботи із заявками.");
        var page = await store.ListAdminsAsync(cursor, ct);
        return new(page.Items.Where(admin => admin.IsActive).Select(admin => new Assignee(admin.UserId, admin.Email)).ToArray(), page.Cursor);
    }

    private async Task<AdminUser> GetAdminForChangeAsync(AdminUser actor, string targetId, int version, CancellationToken ct)
    {
        var target = await store.GetAdminAsync(targetId, ct) ?? throw new AdminOperationException(AdminFailure.NotFound, "Користувача не знайдено.");
        if (target.IsSuperAdmin || target.UserId == actor.UserId)
            throw new AdminOperationException(AdminFailure.Forbidden, "Цей обліковий запис керується через AWS.");
        RequireVersion(target, version);
        return target;
    }

    private static AdminUser RequireActive(AdminUser? actor) =>
        actor?.IsActive == true ? actor : throw new AdminOperationException(AdminFailure.Forbidden, "Адміністративний доступ ще не надано.");

    private static void RequireVersion(AdminUser target, int version)
    {
        if (version != target.Version) throw new AdminOperationException(AdminFailure.Conflict, "Профіль змінено. Оновіть список.");
    }
}
