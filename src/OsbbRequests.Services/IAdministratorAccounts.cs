using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public interface IAdministratorAccounts
{
    Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct);
    Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct);
}
