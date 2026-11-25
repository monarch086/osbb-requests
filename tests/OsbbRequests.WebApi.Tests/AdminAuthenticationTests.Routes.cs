using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OsbbRequests.Domain.Admin;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    [Fact]
    public async Task AdministrativeRoutesAllRequireCognitoAuthentication()
    {
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "me"),
            (HttpMethod.Get, "statistics"),
            (HttpMethod.Get, "messages"),
            (HttpMethod.Post, "messages"),
            (HttpMethod.Get, "administrators"),
            (HttpMethod.Patch, "administrators/target"),
            (HttpMethod.Patch, "administrators/target/permissions"),
            (HttpMethod.Patch, "administrators/target/notifications"),
            (HttpMethod.Get, "assignees"),
            (HttpMethod.Get, "requests"),
            (HttpMethod.Get, "requests/export"),
            (HttpMethod.Get, "requests/42/5"),
            (HttpMethod.Get, "requests/42/5/history"),
            (HttpMethod.Patch, "requests/42/5"),
            (HttpMethod.Get, "residents"),
            (HttpMethod.Get, "residents/directory"),
            (HttpMethod.Get, "residents/export"),
            (HttpMethod.Get, "residents/keys"),
            (HttpMethod.Patch, "residents/keys/42/5/0"),
            (HttpMethod.Get, "residents/" + Reference),
            (HttpMethod.Get, "registrations"),
            (HttpMethod.Get, "registrations/count"),
            (HttpMethod.Get, "registrations/directory"),
            (HttpMethod.Get, "registrations/export"),
            (HttpMethod.Get, $"registrations/{Reference}/approval"),
            (HttpMethod.Get, $"registrations/{Reference}/history"),
            (HttpMethod.Post, $"registrations/{Reference}/decision"),
            (HttpMethod.Patch, $"registrations/{Reference}/data"),
            (HttpMethod.Patch, $"registrations/{Reference}/type"),
            (HttpMethod.Get, "apartment-reviews"),
            (HttpMethod.Get, "apartment-reviews/count"),
            (HttpMethod.Post, $"apartment-reviews/{Reference}/{Reference}")
        };

        using var factory = CreateHost();
        using var client = factory.CreateClient();
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, "/admin/" + path);
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}: {response.StatusCode}");
        }
        Assert.Equal(0, accounts.Reads);
    }

    [Fact]
    public async Task MissingScopeDoesNotLoadAnAdministratorForProtectedActions()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken("scope"));

        using var response = await client.GetAsync("/admin/messages");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, accounts.Reads);
    }

    [Fact]
    public async Task InactiveAdministratorCanReadTheirProfileButCannotUseAdministrativeActions()
    {
        using var factory = CreateHost();
        using var client = AuthenticatedClient(factory, super: false);
        accounts.Admin = accounts.Admin! with { IsActive = false };

        using var me = await client.GetAsync("/admin/me");
        using var denied = await client.GetAsync("/admin/requests");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("Адміністративний доступ ще не надано.", await ReadErrorAsync(denied));
    }

    [Theory]
    [InlineData("test-user", false, HttpStatusCode.BadRequest)]
    [InlineData("other", false, HttpStatusCode.Forbidden)]
    [InlineData("other", true, HttpStatusCode.BadRequest)]
    public async Task NotificationPolicyUsesTheRouteTargetAndLoadsTheActorOnce(string target, bool super, HttpStatusCode expected)
    {
        using var db = new FakeDynamo();
        using var factory = CreateHostWithDynamo(db);
        using var client = AuthenticatedClient(factory, super);
        using var body = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await client.PatchAsync($"/admin/administrators/{target}/notifications", body);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, accounts.Reads);
        Assert.Null(db.LastWrite);
        if (expected == HttpStatusCode.Forbidden)
            Assert.Equal("Недостатньо прав для зміни сповіщень.", await ReadErrorAsync(response));
    }

    [Fact]
    public async Task RequestListAndExportUseSharedDynamoFiltersAndReturnExpectedFormats()
    {
        using var db = new FakeDynamo();
        using var factory = CreateHostWithDynamo(db);
        using var client = AuthenticatedClient(factory, super: false);

        using var response = await client.GetAsync("/admin/requests?section=complex&status=New,InProgress&page=2&includeCounts=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, page!.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("counts").ValueKind);
        Assert.Contains(db.LastQuery!.ExpressionAttributeValues.Values, value => value.S == "InProgress");

        using var export = await client.GetAsync("/admin/requests/export?section=complex");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        Assert.True(export.Headers.CacheControl?.NoStore);
        using var workbook = new ZipArchive(await export.Content.ReadAsStreamAsync());
        Assert.NotNull(workbook.GetEntry("xl/worksheets/sheet1.xml"));

        using var missing = await client.GetAsync("/admin/requests/42/5");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task BroadcastChecksPermissionsBeforeParsingAndWritesAnAtomicOutbox()
    {
        using var db = new FakeDynamo();
        using var factory = CreateHostWithDynamo(db);

        using var forbiddenClient = AuthenticatedClient(factory, super: false);
        using var malformedBody = new StringContent("{", Encoding.UTF8, "application/json");
        using var denied = await forbiddenClient.PostAsync("/admin/messages", malformedBody);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("Недостатньо прав для роботи з повідомленнями.", await ReadErrorAsync(denied));
        Assert.Null(db.LastWrite);

        using var authorized = AuthenticatedClient(factory, super: true);
        using var invalidBody = JsonContent.Create(new { id = Guid.NewGuid(), text = "Оголошення", actor = "forged" });
        using var invalid = await authorized.PostAsync("/admin/messages", invalidBody);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null(db.LastWrite);

        const string id = "187582e9-77a5-47cd-96a9-bc944ead67bb";
        using var body = JsonContent.Create(new { id, text = "  Тестове оголошення  " });
        using var response = await authorized.PostAsync("/admin/messages", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<BroadcastMessage>();
        Assert.Equal("test-user", message?.CreatedById);
        Assert.Equal("Тестове оголошення", message?.Text);
        Assert.Equal(4, db.LastWrite?.TransactItems.Count);
        Assert.Contains(db.LastWrite!.TransactItems, item => item.Put?.Item["pk"].S == "NOTIFICATIONS#osbb-requests-dev");
    }

    private const string Reference = "0123456789abcdef0123456789abcdef";

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("error").GetString();
    }

    private WebApplicationFactory<Program> CreateHostWithDynamo(FakeDynamo db) =>
        CreateHost().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAmazonDynamoDB>();
            services.AddSingleton<IAmazonDynamoDB>(db);
        }));

    private HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory, bool super)
    {
        accounts.Admin = new("test-user", "admin@example.invalid", "Admin", true, super, 9);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken());
        return client;
    }

    private sealed class FakeDynamo : AmazonDynamoDBClient
    {
        public FakeDynamo() : base(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1) { }
        public QueryRequest? LastQuery { get; private set; }
        public TransactWriteItemsRequest? LastWrite { get; private set; }

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            LastQuery = request;
            return Task.FromResult(new QueryResponse { Items = [] });
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetItemResponse { Item = [] });

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            LastWrite = request;
            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }
}
