using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OsbbRequests.Data.Storage;
using OsbbRequests.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using Xunit;
using BroadcastMessage = OsbbRequests.Domain.Admin.BroadcastMessage;

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
        using var factory = CreateHost();
        using var client = AuthenticatedClient(factory, super);
        using var body = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await client.PatchAsync($"/admin/administrators/{target}/notifications", body);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(1, accounts.Reads);
        if (expected == HttpStatusCode.Forbidden)
            Assert.Equal("Недостатньо прав для зміни сповіщень.", await ReadErrorAsync(response));
    }

    [PostgresFact]
    public async Task RequestListAndExportUsePostgresFiltersAndReturnExpectedFormats()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var factory = CreateHostWithPostgres(db);
        using var client = AuthenticatedClient(factory, super: false);

        using var response = await client.GetAsync("/admin/requests?section=complex&status=New,InProgress&page=2&includeCounts=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(2, page!.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.RootElement.GetProperty("counts").ValueKind);
        Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());

        using var export = await client.GetAsync("/admin/requests/export?section=complex");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        Assert.True(export.Headers.CacheControl?.NoStore);
        using var workbook = new ZipArchive(await export.Content.ReadAsStreamAsync());
        Assert.NotNull(workbook.GetEntry("xl/worksheets/sheet1.xml"));

        using var missing = await client.GetAsync("/admin/requests/42/5");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgresFact]
    public async Task BroadcastChecksPermissionsBeforeParsingAndWritesAnAtomicOutbox()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var factory = CreateHostWithPostgres(db);

        using var forbiddenClient = AuthenticatedClient(factory, super: false);
        using var malformedBody = new StringContent("{", Encoding.UTF8, "application/json");
        using var denied = await forbiddenClient.PostAsync("/admin/messages", malformedBody);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("Недостатньо прав для роботи з повідомленнями.", await ReadErrorAsync(denied));
        Assert.False(await db.Readout.BroadcastMessages.AnyAsync());

        using var authorized = AuthenticatedClient(factory, super: true);
        using var invalidBody = JsonContent.Create(new { id = Guid.NewGuid(), text = "Оголошення", actor = "forged" });
        using var invalid = await authorized.PostAsync("/admin/messages", invalidBody);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.False(await db.Readout.BroadcastMessages.AnyAsync());

        const string id = "187582e9-77a5-47cd-96a9-bc944ead67bb";
        using var body = JsonContent.Create(new { id, text = "  Тестове оголошення  " });
        using var response = await authorized.PostAsync("/admin/messages", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = await response.Content.ReadFromJsonAsync<BroadcastMessage>();
        Assert.Equal("test-user", message?.CreatedById);
        Assert.Equal("Тестове оголошення", message?.Text);
        Assert.Equal(id, (await db.Readout.BroadcastMessages.SingleAsync()).MessageId);
        Assert.Equal("LOCAL_PENDING", (await db.Readout.Notifications.SingleAsync()).Kind);
        using var retry = await authorized.PostAsJsonAsync("/admin/messages", new { id, text = "Тестове оголошення" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, await db.Readout.Notifications.CountAsync());
    }

    [PostgresFact]
    public async Task RequestEditsStayInPostgresWithHistoryOutboxAndVersionChecks()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var factory = CreateHostWithPostgres(db);
        using var client = AuthenticatedClient(factory, super: true);
        var change = new { status = "Completed", visibility = "Public", priority = "High", scope = "Complex", version = 2, resolution = "Виконано" };

        using var saved = await client.PatchAsJsonAsync("/admin/requests/42/6", change);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var request = await db.Readout.Requests.SingleAsync();
        Assert.Equal(3, request.Version);
        Assert.Equal(RequestStatus.Completed, request.Status);
        Assert.Equal("LOCAL_PENDING", (await db.Readout.Notifications.SingleAsync()).Kind);
        using var history = await client.GetFromJsonAsync<JsonDocument>("/admin/requests/42/6/history");
        var entry = Assert.Single(history!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Completed", entry.GetProperty("status").GetString());
        Assert.Equal("Виконано", entry.GetProperty("resolution").GetString());

        using var stale = await client.PatchAsJsonAsync("/admin/requests/42/6", change);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await db.Readout.RequestEvents.CountAsync());
    }

    [PostgresFact]
    public async Task ResidentApprovalAndKeyChangesAreVisibleInTheLocalDirectory()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Readout.Residents.Add(new()
        {
            Id = 24,
            ComplexId = 1,
            TelegramUserId = 42,
            RegistrationId = Reference,
            FirstName = "Тест",
            LastName = "Мешканець",
            BuildingNumber = "4Б",
            Stage = RegistrationStage.Pending,
            Version = 2,
            ResidentType = ResidentType.Owner
        });
        db.Readout.ResidentPhones.Add(new() { ResidentId = 24, IsPrimary = true, Phone = "+380670000001" });
        db.Readout.ResidentApartments.Add(new()
        {
            ComplexId = 1,
            ResidentId = 24,
            TelegramUserId = 42,
            RegistrationId = Reference,
            ApartmentId = "fedcba9876543210fedcba9876543210",
            BuildingNumber = "4Д",
            ApartmentNumber = 3,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        db.Readout.Requests.Add(new()
        {
            SourceId = 1,
            ComplexId = 1,
            OwnerId = 42,
            RequestId = 7,
            Version = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            Category = RequestCategories.KeyOrder,
            Status = RequestStatus.Completed
        });
        db.Readout.RequestKeyItems.Add(new()
        {
            SourceId = 1,
            OwnerId = 42,
            RequestId = 7,
            Type = "Магніт",
            Phone = "+380670000001",
            SerialNumber = "one"
        });
        await db.Readout.SaveChangesAsync();
        db.Readout.ChangeTracker.Clear();
        using var factory = CreateHostWithPostgres(db);
        using var client = AuthenticatedClient(factory, super: true);

        using var approval = await client.PostAsJsonAsync($"/admin/registrations/{Reference}/decision", new { approved = true, version = 2 });
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        using var residents = await client.GetFromJsonAsync<JsonDocument>("/admin/residents/directory?building=4Б&compact=true");
        Assert.Equal(1, residents!.RootElement.GetProperty("total").GetInt32());
        var membership = await db.Readout.ResidentMemberships.SingleAsync();
        Assert.True(membership.Active);

        var keys = await client.GetFromJsonAsync<ResidentKeyPage>("/admin/residents/keys");
        Assert.Equal("Магніт", Assert.Single(Assert.Single(keys!.Items).Keys).Type);
        using var edit = await client.PatchAsJsonAsync("/admin/residents/keys/42/7/0", new { type = "Перепустка", serialNumber = "two", version = 3 });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var history = await client.GetFromJsonAsync<Page<ResidentTypeChange>>($"/admin/registrations/{Reference}/history");
        Assert.Equal("two", Assert.Single(history!.Items).KeyChange?.Current?.SerialNumber);
        using var remove = await client.PatchAsJsonAsync("/admin/residents/keys/42/7/0", new { remove = true, version = 4 });
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ResidentKeyPage>("/admin/residents/keys"))!.Items);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var reviews = await client.GetFromJsonAsync<Page<ApartmentReviewEntry>>("/admin/apartment-reviews", jsonOptions);
        var apartment = Assert.Single(Assert.Single(reviews!.Items).Apartments);
        using var apartmentDecision = await client.PostAsJsonAsync($"/admin/apartment-reviews/{Reference}/{apartment.Id}", new { approved = true });
        Assert.Equal(HttpStatusCode.OK, apartmentDecision.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<Page<ApartmentReviewEntry>>("/admin/apartment-reviews", jsonOptions))!.Items);
    }

    private const string Reference = "0123456789abcdef0123456789abcdef";

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("error").GetString();
    }

    private WebApplicationFactory<Program> CreateHostWithPostgres(TestDatabase db) =>
        CreateHost().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<DataContext>>();
            services.AddSingleton<IDbContextFactory<DataContext>>(db);
        }));

    private HttpClient AuthenticatedClient(WebApplicationFactory<Program> factory, bool super)
    {
        accounts.Admin = new("test-user", "admin@example.invalid", "Admin", true, super, 9);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken());
        return client;
    }

    public sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OSBB_POSTGRES_TEST_CONNECTION")))
                Skip = "A PostgreSQL test server is required.";
        }
    }

    private sealed class TestDatabase(string connectionString, string database, string serverConnection)
        : IDbContextFactory<DataContext>, IAsyncDisposable
    {
        public DataContext Readout { get; } = new(CreateOptions(connectionString));

        public static async Task<TestDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable("OSBB_POSTGRES_TEST_CONNECTION")!;
            var name = "osbb_test_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(server, $"CREATE DATABASE {name}");
            var connection = new NpgsqlConnectionStringBuilder(server) { Database = name, Pooling = false };
            var holder = new TestDatabase(connection.ConnectionString, name, server);
            try
            {
                await holder.Readout.Database.EnsureCreatedAsync();
                var context = holder.Readout;
                context.ImportSources.Add(new() { Id = 1, Region = "eu-central-1", Stage = "dev", AwsAccountId = "000000000000" });
                context.Complexes.Add(new() { Id = 1, SourceId = 1, ExternalId = "osbb-requests-dev" });
                context.Administrators.Add(new() { SourceId = 1, UserId = "test-user", Email = "admin@example.invalid", IsActive = true, Version = 9 });
                context.SourceRecords.Add(new()
                {
                    SourceId = 1,
                    Table = "administrators",
                    Key = "test-user",
                    Hash = "fixture",
                    Data = "{\"IsSuperAdmin\":true}"
                });
                context.Requests.Add(new()
                {
                    SourceId = 1,
                    ComplexId = 1,
                    OwnerId = 42,
                    RequestId = 6,
                    Version = 2,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Category = "Інше",
                    Status = RequestStatus.InProgress,
                    Visibility = RequestVisibility.Public,
                    Scope = RequestScope.Complex,
                    Description = "Тестова заявка"
                });
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
                return holder;
            }
            catch
            {
                await holder.DisposeAsync();
                throw;
            }
        }

        public DataContext CreateDbContext() => new(CreateOptions(connectionString));

        private static DbContextOptions<DataContext> CreateOptions(string connection) =>
            new DbContextOptionsBuilder<DataContext>().UseNpgsql(connection).Options;

        public async ValueTask DisposeAsync()
        {
            await Readout.DisposeAsync();
            await ExecuteAsync(serverConnection, $"DROP DATABASE {database}");
        }

        private static async Task ExecuteAsync(string connection, string sql)
        {
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();
            await using var command = new NpgsqlCommand(sql, database);
            await command.ExecuteNonQueryAsync();
        }
    }
}
