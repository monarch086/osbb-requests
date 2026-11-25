using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public interface IAdministratorStore : IAdministratorDirectory
{
    Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct);
    Task SetNotificationSettingsAsync(AdminUser actor, AdminUser target, NotificationSettingsChange change, CancellationToken ct);
    Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct);
    Task SetPermissionsAsync(AdminUser actor, AdminUser target, PermissionsChange change, CancellationToken ct);
}
