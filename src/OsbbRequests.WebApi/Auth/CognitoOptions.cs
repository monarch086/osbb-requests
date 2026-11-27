using System.Text.RegularExpressions;

namespace OsbbRequests.WebApi.Auth;

public sealed partial class CognitoOptions
{
    public string UserPoolId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Region => UserPoolId.Split('_', 2)[0];
    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";

    public bool IsValid() => PoolIdPattern().IsMatch(UserPoolId) && ClientIdPattern().IsMatch(ClientId);

    [GeneratedRegex(@"\A[a-z]{2}(?:-[a-z0-9]+)+-[0-9]+_[A-Za-z0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PoolIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdPattern();
}
