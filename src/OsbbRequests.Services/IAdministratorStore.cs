using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

public interface IAdministratorStore : IAdministratorDirectory
{
    Task SetNotificationSettingsAsync(AdminUser actor, AdminUser target, NotificationSettingsChange change, CancellationToken ct);
    Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct);
    Task SetPermissionsAsync(AdminUser actor, AdminUser target, PermissionsChange change, CancellationToken ct);
}
