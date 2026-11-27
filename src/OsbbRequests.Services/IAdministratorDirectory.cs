using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

public interface IAdministratorDirectory
{
    Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct);
    Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct);
}
