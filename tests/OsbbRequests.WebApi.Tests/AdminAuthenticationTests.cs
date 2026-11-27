using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests : IDisposable
{
    private const string Pool = "eu-central-1_TestPool";
    private const string Client = "testclient";
    private const string Issuer = "https://cognito-idp.eu-central-1.amazonaws.com/" + Pool;
    private const string Scope = "aws.cognito.signin.user.admin";
    private readonly RSA rsa = RSA.Create(2048);
    private readonly FakeAccounts accounts = new();

    [Fact]
    public async Task HealthIsPublicAndUnauthenticatedMeNeverReadsAccounts()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/me")).StatusCode);
        Assert.Equal(0, accounts.Reads);
    }

    [Theory]
    [InlineData("issuer", HttpStatusCode.Unauthorized)]
    [InlineData("signature", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("client", HttpStatusCode.Unauthorized)]
    [InlineData("audience", HttpStatusCode.Unauthorized)]
    [InlineData("id-token", HttpStatusCode.Unauthorized)]
    [InlineData("subject", HttpStatusCode.Unauthorized)]
    [InlineData("scope", HttpStatusCode.Forbidden)]
    public async Task InvalidTokensAndScopeNeverReachTheStore(string violation, HttpStatusCode expected)
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();
        using var response = await GetMeAsync(client, SignedToken(violation));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, accounts.Reads);
    }

    [Fact]
    public async Task CorrectAccessTokenReturnsExistingAdminWithoutCognitoLookup()
    {
        accounts.Admin = new("test-user", "admin@example.invalid", "Admin", true, false, 9);
        using var factory = CreateHost();
        using var client = factory.CreateClient();
        using var response = await GetMeAsync(client, SignedToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(accounts.Admin, await response.Content.ReadFromJsonAsync<AdminUser>());
        Assert.Equal(0, accounts.Registrations);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task CognitoEmailVerificationControlsFirstRegistration(bool verified, HttpStatusCode expected)
    {
        var identity = new FakeCognito(verified);
        using var factory = CreateHost(identity);
        using var client = factory.CreateClient();
        var accessToken = SignedToken();
        using var response = await GetMeAsync(client, accessToken);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(accessToken, identity.ReceivedToken);
        Assert.Equal(verified ? 1 : 0, accounts.Registrations);
        if (verified)
        {
            var admin = await response.Content.ReadFromJsonAsync<AdminUser>();
            Assert.Equal("new@example.invalid", admin?.Email);
            Assert.False(admin?.IsActive);
            Assert.False(admin?.IsSuperAdmin);
        }
    }

    private WebApplicationFactory<Program> CreateHost(FakeCognito? cognito = null)
    {
        var key = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "test-key" };
        var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
        metadata.SigningKeys.Add(key);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Cognito:UserPoolId", Pool);
            builder.UseSetting("Cognito:ClientId", Client);
            builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=unused");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
                services.RemoveAll<IAdministratorAccounts>();
                services.AddSingleton<IAdministratorAccounts>(accounts);
                if (cognito is not null)
                {
                    services.RemoveAll<IAmazonCognitoIdentityProvider>();
                    services.AddSingleton<IAmazonCognitoIdentityProvider>(cognito);
                }
                else
                {
                    services.RemoveAll<IIdentityProvider>();
                    services.AddSingleton<IIdentityProvider, UnexpectedIdentityLookup>();
                }
            });
        });
    }

    private string SignedToken(string? violation = null, string userId = "test-user")
    {
        using var foreignKey = RSA.Create(2048);
        var key = new RsaSecurityKey(violation == "signature" ? foreignKey : rsa) { KeyId = "test-key" };
        var claims = new List<Claim>
        {
            new("client_id", violation == "client" ? "wrongclient" : Client),
            new("token_use", violation == "id-token" ? "id" : "access"),
            new("scope", violation == "scope" ? "openid " + Scope + ".other" : "openid " + Scope)
        };
        if (violation != "subject") claims.Add(new Claim("sub", userId));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(violation == "issuer" ? Issuer + "-wrong" : Issuer,
            violation == "audience" ? "wrong-audience" : null, claims, now.AddMinutes(-2),
            violation == "expired" ? now.AddSeconds(-10) : now.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<HttpResponseMessage> GetMeAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public void Dispose() => rsa.Dispose();

    private sealed class FakeAccounts : IAdministratorAccounts
    {
        public int Reads { get; private set; }
        public int Registrations { get; private set; }
        public AdminUser? Admin { get; set; }

        public Task<AdminUser?> GetAdminAsync(string userId, CancellationToken ct)
        {
            Reads++;
            Assert.Equal("test-user", userId);
            return Task.FromResult(Admin);
        }

        public Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct)
        {
            Registrations++;
            return Task.FromResult(new AdminUser(identity.UserId, identity.Email, identity.DisplayName, false, false, 1));
        }
    }

    private sealed class UnexpectedIdentityLookup : IIdentityProvider
    {
        public Task<Identity> GetAsync(string accessToken, string expectedUserId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Existing administrators do not require a Cognito lookup.");
    }

    private sealed class FakeCognito(bool verified) : AmazonCognitoIdentityProviderClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public string? ReceivedToken { get; private set; }

        public override Task<GetUserResponse> GetUserAsync(GetUserRequest request, CancellationToken cancellationToken = default)
        {
            ReceivedToken = request.AccessToken;
            return Task.FromResult(new GetUserResponse
            {
                UserAttributes =
                [
                    new() { Name = "sub", Value = "test-user" },
                    new() { Name = "email", Value = "New@example.invalid" },
                    new() { Name = "email_verified", Value = verified ? "true" : "false" }
                ]
            });
        }
    }
}
