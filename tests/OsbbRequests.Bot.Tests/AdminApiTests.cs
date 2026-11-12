using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedFailuresNotifyServiceChatAndKeepGeneric503Response(bool serviceFails)
    {
        var store = new FakeStore { ReadError = new InvalidOperationException("SECRET private resident text") };
        var notifier = new RecordingServiceNotifier(serviceFails);
        var response = await new OsbbRequests.AdminApi.Function(Handler(store), notifier)
            .FunctionHandler(Request("/admin/residents"), ContextProxy.Create());
        Assert.Equal(503, response.StatusCode);
        Assert.DoesNotContain("SECRET", response.Body);
        Assert.DoesNotContain("private resident text", response.Body);
        Assert.Equal("admin_api", Assert.Single(notifier.Errors).Operation);
    }

    [Fact]
    public async Task ExpectedAccessErrorsDoNotNotifyServiceChat()
    {
        var notifier = new RecordingServiceNotifier();
        var response = await new OsbbRequests.AdminApi.Function(Handler(new FakeStore()), notifier)
            .FunctionHandler(Request("/admin/residents", user: "target"), ContextProxy.Create());
        Assert.Equal(403, response.StatusCode);
        Assert.Empty(notifier.Errors);
    }
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
            routeKey = method == "GET" && path is "/admin/requests/export" or "/admin/residents/export" or "/admin/registrations/export" ? "GET " + path : "ANY /admin/{proxy+}",
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
    public async Task PendingCountRequiresActiveAdminAndReturnsOnlyCount()
    {
        var store = new FakeStore();
        var anonymous = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations/count", user: null), default));
        Assert.Equal(401, anonymous.Status);
        var inactive = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations/count", user: "target"), default));
        Assert.Equal(403, inactive.Status);
        var response = await Handler(store).HandleAsync(Request("/admin/registrations/count"), default);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        Assert.Single(json.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task PagePermissionsProtectApisAndRequestDetailsIndependently()
    {
        var store = new FakeStore();
        store.Users["regular"] = Regular with { Permissions = new(false, true, false, true, true) };
        var handler = Handler(store);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => handler.HandleAsync(Request("/admin/residents"), default))).Status);
        Assert.Equal(200, (await handler.HandleAsync(Request("/admin/registrations"), default)).StatusCode);
        Assert.Equal(200, (await handler.HandleAsync(Request("/admin/messages"), default)).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => handler.HandleAsync(Request("/admin/requests", query: new() { ["section"] = "complex" }), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => handler.HandleAsync(Request("/admin/requests"), default))).Status);
        Assert.Equal(200, (await handler.HandleAsync(Request("/admin/requests", query: new() { ["section"] = "keys" }), default)).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => handler.HandleAsync(Request("/admin/requests/99/7/history"), default))).Status);
        store.ForcedRequest = new(7, 99, "building", RequestCategories.KeyOrder, "", "Магніт", DateTimeOffset.UtcNow);
        Assert.Equal(200, (await handler.HandleAsync(Request("/admin/requests/99/7/history"), default)).StatusCode);
    }

    [Fact]
    public async Task OnlySuperAdminCanChangePermissionsAndStaleVersionIsRejected()
    {
        var store = new FakeStore();
        var change = new { version = 1, permissions = new { complexRequests = false, keyOrders = true, registeredResidents = false, pendingResidents = true, messages = false } };
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/administrators/target/permissions", "PATCH", body: change), default))).Status);
        Assert.Equal(200, (await Handler(store).HandleAsync(Request("/admin/administrators/target/permissions", "PATCH", user: "super", body: change), default)).StatusCode);
        Assert.Equal(1, store.PermissionWrites);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/administrators/target/permissions", "PATCH", user: "super", body: new { version = 0, permissions = change.permissions }), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/administrators/super/permissions", "PATCH", user: "super", body: change), default))).Status);
    }

    [Fact]
    public async Task PendingResidentTypeChangeRequiresPendingPermission()
    {
        var id = new string('a', 32);
        var store = new FakeStore();
        store.Users["regular"] = Regular with { Permissions = new(false, false, false, true, false) };
        Assert.Equal(200, (await Handler(store).HandleAsync(Request("/admin/registrations/" + id + "/type", "PATCH", body: new { version = 1, residentType = "Employee" }), default)).StatusCode);
        Assert.Equal(1, store.TypeChanges);
        store.Users["regular"] = Regular with { Permissions = new(false, false, true, false, false) };
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/registrations/" + id + "/type", "PATCH", body: new { version = 1, residentType = "Employee" }), default))).Status);
        Assert.Equal(1, store.TypeChanges);
    }

    [Theory]
    [InlineData(true, false, 200)]
    [InlineData(false, true, 403)]
    public async Task EditingResidentDataRequiresPendingPermission(bool pending, bool registered, int expected)
    {
        var store = new FakeStore();
        store.Users["regular"] = Regular with { Permissions = new(false, false, registered, pending, false) };
        var request = Request("/admin/registrations/" + new string('a', 32) + "/data", "PATCH", body: new { version = 1, firstName = "Олена", lastName = "Тестова", residentType = "Owner" });
        if (expected == 200) Assert.Equal(200, (await Handler(store).HandleAsync(request, default)).StatusCode);
        else Assert.Equal(expected, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
        Assert.Equal(pending ? 1 : 0, store.TypeChanges);
    }

    [Theory]
    [InlineData(RegistrationStage.Approved, true, 200)]
    [InlineData(RegistrationStage.Approved, false, 403)]
    [InlineData(RegistrationStage.Pending, true, 404)]
    [InlineData(RegistrationStage.Deleted, true, 404)]
    public async Task ApprovalDetailsRequireRegisteredResidentsPermissionAndApprovedRegistration(RegistrationStage stage, bool permitted, int expected)
    {
        var store = new FakeStore { HistoryStage = stage };
        store.Users["regular"] = Regular with { Permissions = new(false, false, permitted, true, false) };
        var request = Request("/admin/registrations/" + new string('a', 32) + "/approval");
        if (expected == 200)
        {
            var response = await Handler(store).HandleAsync(request, default);
            Assert.Equal(200, response.StatusCode);
            using var json = JsonDocument.Parse(response.Body);
            Assert.Equal("admin@example.org", json.RootElement.GetProperty("actor").GetString());
        }
        else Assert.Equal(expected, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
    }

    [Theory]
    [InlineData(RegistrationStage.Pending, true, false, 200)]
    [InlineData(RegistrationStage.Pending, false, true, 403)]
    [InlineData(RegistrationStage.Approved, false, true, 200)]
    [InlineData(RegistrationStage.Approved, true, false, 403)]
    public async Task ResidentHistoryRequiresPermissionForCurrentList(RegistrationStage stage, bool pending, bool registered, int expected)
    {
        var id = new string('a', 32);
        var store = new FakeStore { HistoryStage = stage };
        store.Users["regular"] = Regular with { Permissions = new(false, false, registered, pending, false) };
        var request = Request("/admin/registrations/" + id + "/history");
        if (expected == 200) Assert.Equal(200, (await Handler(store).HandleAsync(request, default)).StatusCode);
        else Assert.Equal(expected, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
    }

    [Theory]
    [InlineData("/admin/residents")]
    [InlineData("/admin/residents/directory")]
    [InlineData("/admin/residents/export")]
    [InlineData("/admin/registrations")]
    [InlineData("/admin/registrations/directory")]
    [InlineData("/admin/registrations/export")]
    [InlineData("/admin/registrations/count")]
    [InlineData("/admin/messages")]
    [InlineData("/admin/assignees")]
    public async Task NoPagePermissionsDenyProtectedResources(string path)
    {
        var store = new FakeStore();
        store.Users["regular"] = Regular with { Permissions = new(false, false, false, false, false) };
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request(path), default));
        Assert.Equal(403, error.Status);
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
    [Fact]
    public async Task KeyOrderCannotBeMadePublic()
    {
        var store = new FakeStore { ForcedRequest = new(7, 99, "building", RequestCategories.KeyOrder, "", "Магніт", DateTimeOffset.UtcNow) };
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/99/7", "PATCH", body:
            new { status = "New", visibility = "Public", resolution = "", assignedTo = "", priority = "Normal", plannedDate = "", version = 1, scope = "Complex" }), default));
        Assert.Equal(400, error.Status);
        Assert.Equal(0, store.RequestWrites);
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
        Assert.Equal("resident@example.com", resident.GetProperty("email").GetString());
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
        Assert.Equal("resident@example.com", resident.GetProperty("email").GetString());
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
    [InlineData("4Б", null, 200)]
    [InlineData("4Б", 1, 200)]
    [InlineData("4Б", 2, 400)]
    [InlineData("4В", null, 200)]
    [InlineData("4В", 1, 200)]
    [InlineData("4Г", 2, 200)]
    [InlineData("4Д", 0, 400)]
    [InlineData("4В", 3, 400)]
    public async Task AdminEntranceValidationPreservesUnspecifiedLegacyValues(string building, int? entrance, int expected)
    {
        var store = new FakeStore();
        var request = Request("/admin/requests/99/1", "PATCH", body: new
        {
            status = "New",
            visibility = "Public",
            priority = "Normal",
            version = 1,
            scope = "Building",
            buildingNumber = building,
            floor = 12,
            entrance
        });
        if (expected == 200)
        {
            Assert.Equal(200, (await Handler(store).HandleAsync(request, default)).StatusCode);
            Assert.Equal(entrance, store.LastRequest!.Entrance);
        }
        else
        {
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
            Assert.Equal(0, store.RequestWrites);
        }
    }

    [Fact]
    public async Task ComplexRequestCannotHaveEntrance()
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/99/1", "PATCH", body: new
        {
            status = "New",
            visibility = "Public",
            priority = "Normal",
            version = 1,
            scope = "Complex",
            buildingNumber = (string?)null,
            floor = (int?)null,
            entrance = 1
        }), default));
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

    [Fact]
    public async Task RequestDirectoryReturnsPageMetadataAndStatistics()
    {
        var store = new FakeStore();
        var response = await Handler(store).HandleAsync(Request("/admin/requests", query: new() { ["page"] = "2", ["section"] = "keys" }), default);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(2, json.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(25, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("counts").GetProperty("total").GetInt32());
        Assert.Equal("keys", store.LastFilters!.Section);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResidentExportIncludesAllRowsAndFiltersWithoutTelegramIds(bool pending)
    {
        var store = new FakeStore();
        var path = pending ? "/admin/registrations/export" : "/admin/residents/export";
        var query = new Dictionary<string, string> { ["building"] = "4Б", ["residentType"] = "Owner", ["sort"] = "lastName", ["direction"] = "asc" };
        var response = await Handler(store).HandleAsync(Request(path, query: query), default);
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsBase64Encoded);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Headers["content-type"]);
        Assert.Equal((pending, "4Б", "Owner", "lastName", "asc"), store.ResidentExportFilters);
        using var archive = new ZipArchive(new MemoryStream(Convert.FromBase64String(response.Body)), ZipArchiveMode.Read);
        using var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var document = XDocument.Load(sheet);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").ToList();
        Assert.Equal(36, rows.Count);
        var text = document.ToString();
        Assert.Contains("Тип мешканця", text);
        Assert.Contains("Власник", text);
        Assert.Contains("4Б", text);
        Assert.Contains("=2+2", text);
        Assert.DoesNotContain("900000000", text);
        Assert.DoesNotContain("Telegram", text);
        Assert.Empty(document.Descendants(ns + "f"));
    }

    [Theory]
    [InlineData("/admin/residents/export")]
    [InlineData("/admin/registrations/export")]
    public async Task ResidentExportRejectsGreedyProxyRoute(string path)
    {
        var request = Request(path);
        request.RouteKey = "ANY /admin/{proxy+}";
        var store = new FakeStore();
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
        Assert.Equal(0, store.PrivateReads);
    }

    [Theory]
    [InlineData("/admin/residents/export", false, true)]
    [InlineData("/admin/registrations/export", true, false)]
    public async Task ResidentExportRequiresPermissionForTheRequestedList(string path, bool registered, bool pending)
    {
        var store = new FakeStore();
        store.Users["regular"] = Regular with { Permissions = new(false, false, registered, pending, false) };
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request(path), default))).Status);
        Assert.Equal(0, store.PrivateReads);
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
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task OnlyActiveSuperAdministratorsCanAccessMessages(string method)
    {
        var store = new FakeStore();
        foreach (var user in new[] { "regular", "target" })
        {
            var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/messages", method, user: user,
                body: new { id = "a51503dd-a080-4a9a-9875-26300c6d702d", text = "Оголошення" }), default));
            Assert.Equal(403, error.Status);
        }
        var result = await Handler(store).HandleAsync(Request("/admin/messages", method, user: "super",
            body: new { id = "a51503dd-a080-4a9a-9875-26300c6d702d", text = "Оголошення" }), default);
        Assert.Equal(200, result.StatusCode);
    }

    [Theory]
    [InlineData("complex", true, false, 200)]
    [InlineData("complex", false, true, 403)]
    [InlineData("keys", true, false, 403)]
    [InlineData("keys", false, true, 200)]
    public async Task TicketExportEnforcesSectionPermissions(string section, bool complex, bool keys, int expected)
    {
        var store = new FakeStore();
        store.Users["regular"] = Regular with { Permissions = new(complex, keys, false, false, false) };
        var request = Request("/admin/requests/export", query: new() { ["section"] = section });
        if (expected == 200) Assert.Equal(200, (await Handler(store).HandleAsync(request, default)).StatusCode);
        else Assert.Equal(403, (await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(request, default))).Status);
    }

    [Fact]
    public async Task TicketExportProducesWorkbookFromAllPagesWithFiltersAndPrivateAuthorData()
    {
        var store = new FakeStore { ExportPages = true };
        var query = new Dictionary<string, string> { ["section"] = "complex", ["status"] = "New,InProgress", ["category"] = "Інше", ["target"] = "Building4Б", ["floor"] = "3", ["search"] = "тест", ["from"] = "2026-01-01", ["to"] = "2026-12-31" };
        var response = await Handler(store).HandleAsync(Request("/admin/requests/export", query: query), default);
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsBase64Encoded);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Headers["content-type"]);
        Assert.Equal(3, store.ExportReads);
        Assert.Equal(2, store.AuthorReads);
        Assert.Equal(new[] { "New", "InProgress" }, store.LastFilters!.Statuses);
        Assert.Equal("Інше", store.LastFilters.Category);
        Assert.Equal("Building4Б", store.LastFilters.Target);
        Assert.Equal(3, store.LastFilters.Floor);
        Assert.Equal("тест", store.LastFilters.Search);
        Assert.Equal("2026-01-01", store.LastFilters.From);
        Assert.Equal("2026-12-31", store.LastFilters.To);
        using var archive = new ZipArchive(new MemoryStream(Convert.FromBase64String(response.Body)), ZipArchiveMode.Read);
        using var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var document = XDocument.Load(sheet);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").Select(row => row.Descendants(ns + "t").Select(t => t.Value).ToArray()).ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal(new[] { "1", "2", "3", "4" }, document.Descendants(ns + "row").Select(row => (string?)row.Attribute("r")));
        Assert.Equal("I2", (string?)document.Descendants(ns + "row").Skip(1).First().Elements(ns + "c").Skip(8).First().Attribute("r"));
        Assert.Equal("Автор", rows[0][8]);
        Assert.Equal("Телефон", rows[0][9]);
        Assert.Equal("Олена Ковальчук", rows[1][8]);
        Assert.Equal("+380500000000", rows[1][9]);
        Assert.Equal("Мешканець видалив свої дані", rows[3][8]);
        Assert.Equal("", rows[3][9]);
        Assert.Equal("=2+2", rows[1][7]);
        Assert.Empty(document.Descendants(ns + "f"));
    }

    [Theory]
    [InlineData("/admin/requests/export/")]
    [InlineData("/admin/requests/export//")]
    public async Task TicketExportCannotBypassThrottledRouteWithTrailingSlashes(string path)
    {
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request(path, query: new() { ["section"] = "complex" }), default));
        Assert.Equal(404, error.Status);
        Assert.Equal(0, store.ExportReads);
    }

    [Fact]
    public async Task TicketExportCannotRunViaGreedyGatewayRoute()
    {
        var request = Request("/admin/requests/export", query: new() { ["section"] = "complex" });
        request.RouteKey = "ANY /admin/{proxy+}";
        var error = await Assert.ThrowsAsync<ApiError>(() => Handler(new FakeStore()).HandleAsync(request, default));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task TicketExportRejectsRepeatedCursorAndInvalidFilters()
    {
        var store = new FakeStore { ExportPages = true, RepeatExportCursor = true };
        var repeated = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/export", query: new() { ["section"] = "complex" }), default));
        Assert.Equal(503, repeated.Status);
        var invalid = await Assert.ThrowsAsync<ApiError>(() => Handler(new FakeStore()).HandleAsync(Request("/admin/requests/export", query: new() { ["section"] = "complex", ["status"] = "wrong" }), default));
        Assert.Equal(400, invalid.Status);
    }

    [Fact]
    public async Task RequestDetailsExposeAuthorOnlyToActiveAdministrators()
    {
        var store = new FakeStore();
        foreach (var user in new string?[] { null, "target" })
        {
            var error = await Assert.ThrowsAsync<ApiError>(() => Handler(store).HandleAsync(Request("/admin/requests/99/7", user: user), default));
            Assert.Equal(user is null ? 401 : 403, error.Status);
        }
        var response = await Handler(store).HandleAsync(Request("/admin/requests/99/7"), default);
        using var json = JsonDocument.Parse(response.Body);
        var author = json.RootElement.GetProperty("author");
        Assert.Equal("Олена", author.GetProperty("firstName").GetString());
        Assert.Equal("4Д", author.GetProperty("buildingNumber").GetString());
        Assert.Equal("125", author.GetProperty("apartmentNumber").GetString());
        Assert.Equal("+380500000000", author.GetProperty("phone").GetString());
        Assert.False(author.TryGetProperty("telegramChatId", out _));
    }

    private sealed class FakeIdentity : IIdentityProvider
    { public Task<Identity> GetAsync(string token, string user, CancellationToken ct) => Task.FromResult(new Identity(user, user + "@example.org", user)); }
    private sealed class FakeStore : IAdminStore
    {
        public RegistrationStage HistoryStage = RegistrationStage.Pending;
        public int TypeChanges;
        public Task<RegistrationApproval?> GetRegistrationApprovalAsync(string reference, CancellationToken ct) => Task.FromResult<RegistrationApproval?>(new("admin@example.org", DateTimeOffset.UnixEpoch, false));
        public Task<ResidentRegistration?> GetRegistrationAsync(string reference, CancellationToken ct) => Task.FromResult<ResidentRegistration?>(new(reference, 99, "building", HistoryStage, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        public Task UpdateResidentDataAsync(AdminUser actor, string reference, ResidentDataChangeRequest change, CancellationToken ct) { TypeChanges++; return Task.CompletedTask; }
        public Task ChangeResidentTypeAsync(AdminUser actor, string reference, ResidentTypeChangeRequest change, CancellationToken ct) { TypeChanges++; return Task.CompletedTask; }
        public Task<Page<ResidentTypeChange>> RegistrationHistoryAsync(string reference, string? cursor, CancellationToken ct) => Task.FromResult(new Page<ResidentTypeChange>([], null));
        public bool ExportPages;
        public bool RepeatExportCursor;
        public int ExportReads;
        public int AuthorReads;
        public int PermissionWrites;
        public Task SetPermissionsAsync(AdminUser actor, AdminUser target, PermissionsChange change, CancellationToken ct) { PermissionWrites++; return Task.CompletedTask; }
        public Exception? ReadError;
        public Task<Page<BroadcastMessage>> ListMessagesAsync(string? cursor, CancellationToken ct) => Task.FromResult(new Page<BroadcastMessage>([], null));
        public Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct) => Task.FromResult(new BroadcastMessage(change.Id, change.Text, actor.Email, actor.UserId, DateTimeOffset.UtcNow));
        public Dictionary<string, AdminUser> Users { get; } = new() { ["super"] = Super, ["regular"] = Regular, ["target"] = new("target", "new@example.org", "New", false, false, 1) };
        public MaintenanceRequest? LastRequest;
        public MaintenanceRequest? ForcedRequest;
        public RequestFilters? LastFilters;
        public MaintenanceRequest? LastHistoryRequest;
        public int SettingsWrites;
        public int PrivateReads; public int AccessWrites; public int RequestWrites;
        public Task<AdminUser?> GetAdminAsync(string user, CancellationToken ct) => ReadError is not null
            ? throw ReadError : Task.FromResult(Users.GetValueOrDefault(user));
        public Task<AdminUser> RegisterAccountAsync(Identity identity, CancellationToken ct)
        { var user = new AdminUser(identity.UserId, identity.Email, identity.DisplayName, false, false, 1); Users.Add(user.UserId, user); return Task.FromResult(user); }
        public Task<Page<AdminUser>> ListAdminsAsync(string? cursor, CancellationToken ct) => Task.FromResult(new Page<AdminUser>(Users.Values.ToList(), null));
        public Task SetNotificationSettingsAsync(AdminUser actor, AdminUser target, NotificationSettingsChange change, CancellationToken ct) { SettingsWrites++; return Task.CompletedTask; }
        public Task SetAccessAsync(AdminUser actor, AdminUser target, AccessChange change, CancellationToken ct)
        { AccessWrites++; Users[target.UserId] = target with { IsActive = change.IsActive }; return Task.CompletedTask; }
        public Task<int> CountRegistrationsAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<ResidentDirectoryPage> ListResidentDirectoryAsync(bool pending, int page, string? buildingNumber, string? residentType, string? sort, string? direction, CancellationToken ct)
        { PrivateReads++; return Task.FromResult(new ResidentDirectoryPage([], page, 25, 0)); }
        public Task<IReadOnlyList<ResidentRegistration>> ListResidentExportAsync(bool pending, string? buildingNumber, string? residentType, string? sort, string? direction, CancellationToken ct)
        {
            PrivateReads++;
            ResidentExportFilters = (pending, buildingNumber, residentType, sort, direction);
            IReadOnlyList<ResidentRegistration> entries = Enumerable.Range(0, 35).Select(i => new ResidentRegistration(i.ToString("D32"), 900000000 + i,
                "building", pending ? RegistrationStage.Pending : RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, "+380500000000", i == 0 ? "=2+2" : "Ім’я", "Прізвище", BuildingNumber: "4Б", ApartmentNumber: "5", ResidentType: ResidentType.Owner)).ToList();
            return Task.FromResult(entries);
        }
        public (bool Pending, string? Building, string? Type, string? Sort, string? Direction) ResidentExportFilters;
        public Task<Page<ResidentRegistration>> ListRegistrationsAsync(string? cursor, CancellationToken ct)
        { PrivateReads++; return Task.FromResult(new Page<ResidentRegistration>([new("reference", 99, "building", RegistrationStage.Pending, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "+380500000000", "Олена", "Ковальчук", Email: "resident@example.com")], null)); }
        public Task<Page<RegisteredResident>> ListResidentsAsync(string? cursor, CancellationToken ct)
        { PrivateReads++; return Task.FromResult(new Page<RegisteredResident>([new("reference", "Олена", "Ковальчук", "+380500000000", DateTimeOffset.UnixEpoch, "99", Email: "resident@example.com")], "next-page")); }
        public Task DecideAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct) => Task.CompletedTask;
        public Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct)
        {
            LastFilters = filters;
            if (!ExportPages) return Task.FromResult(new Page<ManagedRequest>([], null));
            ExportReads++;
            var date = DateTimeOffset.UnixEpoch;
            if (cursor is null) return Task.FromResult(new Page<ManagedRequest>([new(new(1, 99, "building", "Інше", "", "=2+2", date), 1)], "one"));
            if (cursor == "one") return Task.FromResult(new Page<ManagedRequest>([], RepeatExportCursor ? "one" : "two"));
            return Task.FromResult(new Page<ManagedRequest>([new(new(2, 99, "building", "Інше", "", "Опис", date), 1), new(new(3, 100, "building", "Інше", "", "Опис", date), 1)], null));
        }
        public async Task<AdminRequestPage> ListRequestsWithStatisticsAsync(RequestFilters filters, string? cursor, int pageNumber, CancellationToken ct)
        {
            var page = await ListRequestsAsync(filters, cursor, ct);
            return new(page.Items, page.Cursor, pageNumber, 25, new(page.Items.Count, 0, 0));
        }
        public Task<ManagedRequest?> GetRequestAsync(long owner, long id, CancellationToken ct) => Task.FromResult<ManagedRequest?>(new(ForcedRequest ?? new(id, owner, "building", "інше", "двір", "Опис заявки", DateTimeOffset.UtcNow), 1));
        public Task<RequestAuthor?> GetRequestAuthorAsync(MaintenanceRequest request, CancellationToken ct)
        {
            AuthorReads++;
            return Task.FromResult<RequestAuthor?>(request.OwnerId == 100 ? new(null, null, null, null, true) : new("Олена", "Ковальчук", "4Д", "125", Phone: "+380500000000"));
        }
        public Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct) { RequestWrites++; LastRequest = next; return Task.CompletedTask; }
        public Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(MaintenanceRequest request, CancellationToken ct) { LastHistoryRequest = request; return Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]); }
    }
}
