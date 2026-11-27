using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

public sealed class AdminAccountService(IAdministratorAccounts store, IIdentityProvider identityProvider)
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
        return AdminAccess.RequireActive(actor);
    }
}
