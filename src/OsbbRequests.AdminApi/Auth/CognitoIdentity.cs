using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using OsbbRequests.AdminApi.Models;

namespace OsbbRequests.AdminApi.Auth;

public interface IIdentityProvider
{
    Task<Identity> GetAsync(string accessToken, string expectedUserId, CancellationToken cancellationToken);
}

public sealed class CognitoIdentity(IAmazonCognitoIdentityProvider client) : IIdentityProvider
{
    public async Task<Identity> GetAsync(string accessToken, string expectedUserId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.GetUserAsync(new GetUserRequest { AccessToken = accessToken }, cancellationToken);
            var attributes = result.UserAttributes.ToDictionary(a => a.Name, a => a.Value);
            if (attributes.GetValueOrDefault("sub") != expectedUserId || attributes.GetValueOrDefault("email_verified") != "true")
                throw new ApiError(403, "Підтвердьте електронну адресу.");
            var email = attributes.GetValueOrDefault("email") ?? throw new ApiError(403, "Немає підтвердженої адреси.");
            return new Identity(expectedUserId, email.ToLowerInvariant(), attributes.GetValueOrDefault("name") ?? email);
        }
        catch (NotAuthorizedException)
        {
            throw new ApiError(401, "Сеанс завершено. Увійдіть ще раз.");
        }
    }
}
