using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public interface IIdentityProvider
{
    Task<Identity> GetAsync(string accessToken, string expectedUserId, CancellationToken cancellationToken);
}
