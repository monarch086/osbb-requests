using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace OsbbRequests.WebApi.Auth;

internal static class CognitoAuthentication
{
    public const string AdminScope = "CognitoAdminScope";
    private const string RequiredScope = "aws.cognito.signin.user.admin";

    public static void AddCognitoAuthentication(this IServiceCollection services)
    {
        services.AddOptions<CognitoOptions>().BindConfiguration("Cognito")
            .Validate(options => options.IsValid(), "Configure Cognito:UserPoolId and Cognito:ClientId.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<CognitoOptions>>((jwt, settings) =>
            {
                var config = settings.Value;
                jwt.Authority = config.Issuer;
                jwt.RequireHttpsMetadata = true;
                jwt.MapInboundClaims = false;
                jwt.SaveToken = true;
                jwt.IncludeErrorDetails = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = config.Issuer,
                    ValidateIssuer = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    RequireSignedTokens = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.Zero,
                    // Cognito access tokens from the current client normally carry client_id without aud.
                    ValidateAudience = true,
                    AudienceValidator = (audiences, _, _) =>
                        audiences is null || !audiences.Any() || audiences.Contains(config.ClientId, StringComparer.Ordinal)
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var identity = context.Principal;
                        if (identity?.FindFirst("token_use")?.Value != "access" ||
                            identity.FindFirst("client_id")?.Value != config.ClientId ||
                            string.IsNullOrWhiteSpace(identity.FindFirst("sub")?.Value))
                            context.Fail("Invalid Cognito access token claims.");
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder().AddPolicy(AdminScope, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.FindAll("scope").Any(scope =>
                scope.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(RequiredScope, StringComparer.Ordinal))));
    }
}
