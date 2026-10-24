using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.AdminApi;
using OsbbRequests.AdminApi.Auth;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class AdminApiTests
{
    [Fact]
    public async Task CorsPreflightDoesNotRequireAuthenticationOrReadPrivateData()
    {
        var store = new FakeStore();
        var response = await Handler(store).HandleAsync(Request("/admin/me", "OPTIONS", user: null), default);
        Assert.Equal(204, response.StatusCode);
        Assert.Equal(0, store.PrivateReads);
    }

    private static readonly AdminUser Super = new("super", "head@example.org", "Head", true, true, 1);
    private static readonly AdminUser Regular = new("regular", "admin@example.org", "Admin", true, false, 1);
    private static APIGatewayHttpApiV2ProxyRequest Request(string path, string method = "GET", string? user = "regular", object? body = null, string tokenUse = "access", Dictionary<string, string>? query = null) =>
        JsonSerializer.Deserialize<APIGatewayHttpApiV2ProxyRequest>(JsonSerializer.Serialize(new
        {
            rawPath = path,
            queryStringParameters = query,
            headers = new { authorization = "Bearer example" },
            body = body is null ? null : JsonSerializer.Serialize(body),
            requestContext = new { http = new { method }, authorizer = user is null ? null : new { jwt = new { claims = new Dictionary<string, string> { ["sub"] = user, ["token_use"] = tokenUse } } } }
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private static ApiHandler Handler(FakeStore store) => new(store, new FakeIdentity());

    [Fact]
    public async Task AnonymousAndIdTokensCannotReadPrivateData()
    {
        var store = new FakeStore();
        var anonymous = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations", user: null), default));
        Assert.Equal(401, anonymous.Status);
        var idToken = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations", tokenUse: "id"), default));
        Assert.Equal(401, idToken.Status);
        Assert.Equal(0, store.PrivateReads);
    }
    [Fact]
    public async Task NewAccountIsInactiveAndCannotProcessRequests()
    {
        var store = new FakeStore();
        var me = await Handler(store).HandleAsync(Request("/admin/me", user: "new"), default);
        Assert.Equal(200, me.StatusCode);
        Assert.False(store.Users["new"].IsActive);
        Assert.False(store.Users["new"].IsSuperAdmin);
        var forbidden = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations", user: "new"), default));
        Assert.Equal(403, forbidden.Status);
        Assert.Equal(0, store.PrivateReads);
    }
    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    public async Task RegularAdminCannotManageAdministrators(string method)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(
            Request(method == "GET" ? "/admin/administrators" : "/admin/administrators/target", method,
                body: new { isActive = true, version = 1 }), default));
        Assert.Equal(403, error.Status);
        Assert.Equal(0, store.AccessWrites);
    }
    [Theory]
    [InlineData("isSuperAdmin")]
    [InlineData("IsSuperAdmin")]
    public async Task SuperAdminCannotSetProtectedFlagThroughMassAssignment(string attribute)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/administrators/target", "PATCH", "super",
            new Dictionary<string, object> { ["isActive"] = true, ["version"] = 1, [attribute] = true }), default));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, store.AccessWrites);
        Assert.False(store.Users["target"].IsSuperAdmin);
    }
    [Fact]
    public async Task SuperAdminCanGrantAccessWithoutChangingSuperAdminFlag()
    {
        var store = new FakeStore();
        var result = await Handler(store).HandleAsync(Request("/admin/administrators/target", "PATCH", "super", new { isActive = true, version = 1 }), default);
        Assert.Equal(200, result.StatusCode);
        Assert.True(store.Users["target"].IsActive);
        Assert.False(store.Users["target"].IsSuperAdmin);
    }
    [Fact]
    public async Task RevokedAdminLosesAccessImmediatelyOnNextRequest()
    {
        var store = new FakeStore();
        store.Users["regular"] = Regular with { IsActive = false };
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations"), default));
        Assert.Equal(403, error.Status);
        Assert.Equal(0, store.PrivateReads);
    }
    [Fact]
    public async Task SuperAdminCannotRevokeOwnAccessThroughApi()
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/administrators/super", "PATCH", "super",
            new { isActive = false, version = 1 }), default));
        Assert.Equal(403, error.Status);
        Assert.Equal(0, store.AccessWrites);
    }
    [Fact]
    public async Task ActiveAdminCanReviewResidentRegistrations()
    {
        var store = new FakeStore();
        Assert.Equal(200, (await Handler(store).HandleAsync(Request("/admin/registrations"), default)).StatusCode);
        Assert.Equal(1, store.PrivateReads);
    }
    [Theory]
    [InlineData("super", "regular", 200)]
    [InlineData("super", "super", 200)]
    [InlineData("regular", "regular", 200)]
    [InlineData("regular", "target", 403)]
    [InlineData("target", "target", 403)]
    [InlineData(null, "regular", 401)]
    public async Task NotificationSettingsRequireActiveOwnerOrSuperAdmin(string? user, string target, int status)
    {
        var store = new FakeStore();
        var request = Request("/admin/administrators/" + target + "/notifications", "PATCH", user,
            new { telegramChatId = "99", notifyNewResidents = true, notifyNewRequests = false, notifyNewAdministrators = false, version = 1 });
        if (status == 200)
        {
            Assert.Equal(200, (await Handler(store).HandleAsync(request, default)).StatusCode);
            Assert.Equal(1, store.SettingsWrites);
        }
        else
        {
            Assert.Equal(status, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
            Assert.Equal(0, store.SettingsWrites);
        }
    }

    [Fact]
    public async Task NotificationSettingsCannotChangeProtectedRoleOrUseStaleVersion()
    {
        var store = new FakeStore();
        var protectedChange = Request("/admin/administrators/regular/notifications", "PATCH", "super", new { isSuperAdmin = true, version = 1 });
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(protectedChange, default))).Status);
        var staleChange = Request("/admin/administrators/regular/notifications", "PATCH", "super", new { telegramChatId = "99", version = 0 });
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(staleChange, default))).Status);
        Assert.Equal(0, store.SettingsWrites);
    }

    [Theory]
    [InlineData(null, "access", 401)]
    [InlineData("regular", "id", 401)]
    [InlineData("target", "access", 403)]
    public async Task ResidentDirectoryRequiresActiveAdmin(string? user, string tokenUse, int status)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(
            Request("/admin/residents", user: user, tokenUse: tokenUse), default));
        Assert.Equal(status, error.Status);
        Assert.Equal(0, store.PrivateReads);
    }
    [Fact]
    public async Task RegularAdminCanReadResidentDirectoryWithoutTelegramIds()
    {
        var response = await Handler(new FakeStore()).HandleAsync(Request("/admin/residents"), default);
        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var resident = body.RootElement.GetProperty("items")[0];
        Assert.Equal("Олена", resident.GetProperty("firstName").GetString());
        Assert.False(resident.TryGetProperty("userId", out _));
        Assert.False(resident.TryGetProperty("buildingId", out _));
        Assert.Equal("next-page", body.RootElement.GetProperty("cursor").GetString());
        Assert.Equal("99", resident.GetProperty("telegramChatId").GetString());
    }
    [Theory]
    [InlineData(null, "access", 401)]
    [InlineData("regular", "id", 401)]
    [InlineData("target", "access", 403)]
    public async Task RequestHistoryRequiresActiveAdmin(string? user, string tokenUse, int status)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(
            Request("/admin/requests/99/7/history", user: user, tokenUse: tokenUse), default));
        Assert.Equal(status, error.Status);
        Assert.Null(store.LastHistoryRequest);
    }

    [Fact]
    public async Task HistoryReceivesVerifiedRequestOwnerAndComplex()
    {
        var store = new FakeStore();
        var response = await Handler(store).HandleAsync(Request("/admin/requests/99/7/history"), default);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(99, store.LastHistoryRequest!.OwnerId);
        Assert.Equal(7, store.LastHistoryRequest.Id);
        Assert.Equal("building", store.LastHistoryRequest.BuildingId);
    }

    [Fact]
    public async Task PendingResidentsExposeChatIdOnlyToAuthorizedAdministrators()
    {
        var response = await Handler(new FakeStore()).HandleAsync(Request("/admin/registrations"), default);
        using var body = JsonDocument.Parse(response.Body);
        var resident = body.RootElement.GetProperty("items")[0];
        Assert.Equal("99", resident.GetProperty("telegramChatId").GetString());
        Assert.False(resident.TryGetProperty("userId", out _));
        Assert.False(resident.TryGetProperty("buildingId", out _));
    }

    [Fact]
    public async Task CompletedRequestRequiresExplanation()
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/99/1", "PATCH", body:
            new { status = "Completed", visibility = "Private", resolution = "", priority = "Normal", plannedDate = (string?)null, assignedTo = (string?)null, version = 1 }), default));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, store.RequestWrites);
    }
    [Theory]
    [InlineData("Building", "4Б", 0)]
    [InlineData("Building", "4Б", 1)]
    [InlineData("Building", "4Д", 25)]
    [InlineData("Building", "4В", null)]
    [InlineData("Building", "4Г", 15)]
    [InlineData("Complex", null, null)]
    public async Task AdminCanAssignAnExplicitValidTargetToLegacyRequest(string scope, string? building, int? floor)
    {
        var store = new FakeStore();
        var result = await Handler(store).HandleAsync(Request("/admin/requests/99/1", "PATCH", body:
            new { status = "New", visibility = "Private", priority = "Normal", version = 1, scope, buildingNumber = building, floor }), default);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(store.LastRequest);
        Assert.Equal(scope, store.LastRequest.Scope.ToString());
        Assert.Equal(building, store.LastRequest.BuildingNumber);
        Assert.Equal(floor, store.LastRequest.Floor);
    }
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Building", null, 1)]
    [InlineData("Building", "4А", 1)]
    [InlineData("Building", "1", 1)]
    [InlineData("Building", "4B", 1)]
    [InlineData("Building", "4Б", -1)]
    [InlineData("Building", "4Б", 26)]
    [InlineData("Complex", "4Б", null)]
    [InlineData("Complex", null, 0)]
    [InlineData("Complex", null, 1)]
    public async Task ApiRejectsInvalidOrMissingTarget(string? scope, string? building, int? floor)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/99/1", "PATCH", body:
            new { status = "New", visibility = "Private", priority = "Normal", version = 1, scope, buildingNumber = building, floor }), default));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, store.RequestWrites);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ApiRejectsNumericOrdinalBuildingNumber(int building)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/99/1", "PATCH", body:
            new { status = "New", visibility = "Private", priority = "Normal", version = 1, scope = "Building", buildingNumber = building, floor = 1 }), default));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, store.RequestWrites);
    }
    [Fact]
    public async Task ApiPassesBuildingAndFloorFiltersToStore()
    {
        var store = new FakeStore();
        await Handler(store).HandleAsync(Request("/admin/requests", query: new() { ["target"] = "Building4Д", ["floor"] = "25" }), default);
        Assert.Equal("Building4Д", store.LastFilters!.Target);
        Assert.Equal(25, store.LastFilters.Floor);
    }
    [Fact]
    public async Task EntranceFloorCanBeFilteredWithoutConfusingItWithNoFilter()
    {
        var store = new FakeStore();
        var result = await Handler(store).HandleAsync(Request("/admin/requests", query: new() { ["target"] = "Building4Б", ["floor"] = "0" }), default);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(0, store.LastFilters!.Floor);
    }

    [Theory]
    [InlineData("Building5", "1")]
    [InlineData("Building1", "1")]
    [InlineData("Building4А", "1")]
    [InlineData("Building4Б", "-1")]
    [InlineData("Building4Б", "26")]
    public async Task ApiRejectsInvalidLocationFilters(string target, string floor)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests", query:
            new() { ["target"] = target, ["floor"] = floor }), default));
        Assert.Equal(400, error.Status);
        Assert.Null(store.LastFilters);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("Completed")]
    [InlineData("New")]
    [InlineData("")]
    [InlineData("New,InProgress")]
    [InlineData("Completed,Rejected")]
    [InlineData("New,New")]
    public async Task StatusFilterAcceptsOneOrMultipleStatuses(string? status)
    {
        var store = new FakeStore();
        var query = new Dictionary<string, string>();
        if (status is not null) query["status"] = status;
        await Handler(store).HandleAsync(Request("/admin/requests", query: query), default);
        Assert.Equal(string.IsNullOrEmpty(status) ? [] : status.Split(',').Distinct().ToArray(), store.LastFilters!.Statuses);
    }
    [Theory]
    [InlineData("Unknown")]
    [InlineData("New,Unknown")]
    [InlineData("New,")]
    [InlineData(",New")]
    [InlineData("New,,InProgress")]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("new")]
    public async Task StatusFilterRejectsInvalidValues(string status)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests", query:
            new() { ["status"] = status }), default));
        Assert.Equal(400, error.Status);
        Assert.Null(store.LastFilters);
    }
    private sealed class FakeIdentity : IIdentityProvider
    { public Task<Identity> GetAsync(string token, string user, CancellationToken ct) => Task.FromResult(new Identity(user, user + "@example.org", user)); }
    private sealed class FakeStore : IAdminStore
    {
        public Dictionary<string, AdminUser> Users { get; } = new() { ["super"] = Super, ["regular"] = Regular, ["target"] = new("target", "new@example.org", "New", false, false, 1) };
        public MaintenanceRequest? LastRequest;
        public RequestFilters? LastFilters;
        public MaintenanceRequest? LastHistoryRequest;
        public int SettingsWrites;
        public int PrivateReads; public int AccessWrites; public int RequestWrites;
        public Task<AdminUser?> GetAdminAsync(string user, CancellationToken ct) => Task.FromResult(Users.GetValueOrDefault(user));
        public Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct)
        { var user = new AdminUser(identity.UserId, identity.Email, identity.DisplayName, false, false, 1); Users.Add(user.UserId, user); return Task.FromResult(user); }
        public Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct) => Task.FromResult(new Page<AdminUser>(Users.Values.ToList(), null));
        public Task SetNotificationSettingsAsync(AdminUser actor, AdminUser target, NotificationSettingsChange change, CancellationToken ct) { SettingsWrites++; return Task.CompletedTask; }
        public Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct)
        { AccessWrites++; Users[target.UserId] = target with { IsActive = change.IsActive }; return Task.CompletedTask; }
        public Task<Page<ResidentRegistration>> ListRegistrationsAsync(string? cursor, CancellationToken ct)
        { PrivateReads++; return Task.FromResult(new Page<ResidentRegistration>([new("reference", 99, "building", RegistrationStage.Pending, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "+380500000000", "Олена", "Ковальчук")], null)); }
        public Task<Page<RegisteredResident>> ListResidentsAsync(string? cursor, CancellationToken ct)
        { PrivateReads++; return Task.FromResult(new Page<RegisteredResident>([new("reference", "Олена", "Ковальчук", "+380500000000", DateTimeOffset.UnixEpoch, "99")], "next-page")); }
        public Task DecideAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct) => Task.CompletedTask;
        public Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct) { LastFilters = filters; return Task.FromResult(new Page<ManagedRequest>([], null)); }
        public Task<ManagedRequest?> GetRequestAsync(long owner, long id, CancellationToken ct) => Task.FromResult<ManagedRequest?>(new(new(id, owner, "building", "інше", "двір", "Опис заявки", DateTimeOffset.UtcNow), 1));
        public Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct) { RequestWrites++; LastRequest = next; return Task.CompletedTask; }
        public Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(MaintenanceRequest request, CancellationToken ct) { LastHistoryRequest = request; return Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]); }
    }
}
