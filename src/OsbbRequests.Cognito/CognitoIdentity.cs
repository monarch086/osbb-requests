using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;

namespace OsbbRequests.Cognito;

public sealed class CognitoIdentity(IAmazonCognitoIdentityProvider client) : IIdentityProvider
{
    public async Task<Identity> GetAsync(string accessToken, string expectedUserId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.GetUserAsync(new GetUserRequest { AccessToken = accessToken }, cancellationToken);
            var attributes = result.UserAttributes.ToDictionary(a => a.Name, a => a.Value);
            if (attributes.GetValueOrDefault("sub") != expectedUserId || attributes.GetValueOrDefault("email_verified") != "true")
                throw new AdminOperationException(AdminFailure.Forbidden, "Підтвердьте електронну адресу.");
            var email = attributes.GetValueOrDefault("email") ?? throw new AdminOperationException(AdminFailure.Forbidden, "Немає підтвердженої адреси.");
            return new Identity(expectedUserId, email.ToLowerInvariant(), attributes.GetValueOrDefault("name") ?? email);
        }
        catch (NotAuthorizedException)
        {
            throw new AdminOperationException(AdminFailure.Unauthenticated, "Сеанс завершено. Увійдіть ще раз.");
        }
    }
}
