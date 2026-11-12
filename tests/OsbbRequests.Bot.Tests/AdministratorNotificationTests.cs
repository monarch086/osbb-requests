using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Bot.Notifications;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class AdministratorNotificationTests
{
    [Theory]
    [InlineData(ResidentType.Owner, "Власник", "4Б", RegistrationStage.Pending)]
    [InlineData(ResidentType.FamilyMember, "Член родини", "4В", RegistrationStage.Approved)]
    [InlineData(ResidentType.Tenant, "Орендар", "4Г", RegistrationStage.Approved)]
    [InlineData(ResidentType.Employee, "Працівник ОСББ", null, RegistrationStage.Pending)]
    public void NewResidentAlertContainsTypeAndHouse(ResidentType type, string label, string? house, RegistrationStage stage)
    {
        var resident = new ResidentRegistration("reference", 987654321, "complex", stage, 1, Now, Now,
            Phone: "+380500000000", FirstName: "Олена", LastName: "Тестова", BuildingNumber: house,
            ApartmentNumber: "125", Email: "resident@example.invalid", ResidentType: type);
        var alert = AdministratorNotifications.NewResident(resident);
        Assert.Contains("Тип мешканця: " + label, alert.Text);
        Assert.Contains("Будинок: " + (house ?? "Не зазначено"), alert.Text);
        Assert.Contains(stage == RegistrationStage.Approved ? "схвалено автоматично" : "Потребує перевірки", alert.Text);
        Assert.DoesNotContain("987654321", alert.Text);
        Assert.DoesNotContain(resident.Phone!, alert.Text);
        Assert.DoesNotContain(resident.Email!, alert.Text);
        Assert.Equal(AdministratorEvent.NewResident, alert.AdministratorEvent);
        Assert.True(alert.IsBroadcast);
    }

    [Theory]
    [InlineData(RequestVisibility.Public)]
    [InlineData(RequestVisibility.Private)]
    public void NewRequestAlertIncludesFullDescriptionButNoOtherPrivateFields(RequestVisibility visibility)
    {
        const string description = "Зламано освітлення\nБіля другого ліфта 🛠 <b>Перевірити</b>";
        var request = new MaintenanceRequest(7, 987654321, "complex", "електрика", "", description, Now, Visibility: visibility,
            Resolution: "Приватне пояснення", AssignedTo: "private-assignee", Scope: RequestScope.Building, BuildingNumber: "4Д", Floor: 12, Entrance: 2);
        var alert = AdministratorNotifications.NewRequest(request);
        Assert.Contains("\n~~~~~\n" + description + "\n~~~~~\n", alert.Text);
        Assert.Contains("Нова заявка №7", alert.Text);
        Assert.Contains("Будинок 4Д", alert.Text);
        Assert.DoesNotContain("987654321", alert.Text);
        Assert.DoesNotContain("Приватне пояснення", alert.Text);
        Assert.DoesNotContain("private-assignee", alert.Text);
        Assert.Equal(AdministratorEvent.NewComplexRequest, alert.AdministratorEvent);
        Assert.True(alert.IsBroadcast);
    }

    [Fact]
    public async Task MaximumDescriptionFitsTelegramAndSurvivesAdministratorFanout()
    {
        var description = string.Concat(Enumerable.Repeat("🛠", 1000));
        var request = new MaintenanceRequest(long.MaxValue, 42, "complex", "місця загального користування", "", description, Now,
            Scope: RequestScope.Building, BuildingNumber: "4Д", Floor: 25, Entrance: 2);
        var alert = AdministratorNotifications.NewRequest(request);
        Assert.Contains(description, alert.Text);
        Assert.InRange(alert.Text.Length, 1, 4096);
        using var db = new NotificationDynamo();
        db.Administrators["recipient"] = Administrator();
        db.Page = new() { Items = [new() { ["UserId"] = new("recipient") }] };
        await new DynamoNotificationRepository(db, "data", "complex", "admins")
            .ExpandAsync(new("DUE#event", alert, 0), "lease", Now, default);
        var item = Assert.Single(db.Transaction!.TransactItems, w => w.Put?.Item.ContainsKey("attempts") == true).Put.Item;
        var delivered = JsonSerializer.Deserialize<ResidentNotification>(item["payload"].S)!;
        Assert.Equal(alert.Text, delivered.Text);
        Assert.Equal("recipient", delivered.AdministratorId);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly AdminUser Super = new("super", "head@example.invalid", "Head", true, true, 4);
    private static readonly AdminUser Regular = new("regular", "admin@example.invalid", "Admin", true, false, 2);
    private static NotificationSettingsChange Settings(string? chat = "123") => new(chat, true, true, true, 2);
    private static NotificationWorkItem Broadcast(AdministratorEvent kind = AdministratorEvent.NewRequest) => new("DUE#event",
        new("event", "complex", 0, null, null, "Нова подія", Now, kind), 0);
    private static Dictionary<string, AttributeValue> Administrator(bool active = true, string? chat = "123", bool enabled = true) => new()
    {
        ["UserId"] = new("recipient"),
        ["Email"] = new("admin@example.invalid"),
        ["Version"] = new() { N = "2" },
        ["IsActive"] = new() { BOOL = active },
        ["TelegramChatId"] = chat is null ? new() { NULL = true } : new(chat),
        ["NotifyNewResidents"] = new() { BOOL = enabled },
        ["NotifyNewRequests"] = new() { BOOL = enabled },
        ["NotifyNewAdministrators"] = new() { BOOL = enabled }
    };

    [Theory]
    [InlineData("123", "123")]
    [InlineData(" -100123 ", "-100123")]
    [InlineData("+00123", "123")]
    public async Task SuperAdminCanSaveSettingsWithVersionRoleGuardAndAuditWithoutWritingProtectedFlag(string input, string expected)
    {
        using var db = new NotificationDynamo();
        await new DynamoAdminStore(db, "admins", "data", "complex").SetNotificationSettingsAsync(Super, Regular, Settings(input), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Contains("IsSuperAdmin", Assert.Single(writes, w => w.ConditionCheck is not null).ConditionCheck.ConditionExpression);
        var update = Assert.Single(writes, w => w.Update is not null).Update;
        Assert.Equal(expected, update.ExpressionAttributeValues[":chat"].S);
        Assert.Equal("2", update.ExpressionAttributeValues[":expected"].N);
        Assert.DoesNotContain("IsSuperAdmin", update.UpdateExpression);
        Assert.DoesNotContain("IsActive", update.UpdateExpression);
        Assert.Single(writes, w => w.Put?.Item.GetValueOrDefault("action")?.S == "AdminNotificationSettingsChanged");
    }

    [Fact]
    public async Task PermissionsAreUpdatedWithSuperAdminVersionGuardAndAudit()
    {
        using var db = new NotificationDynamo();
        var permissions = new PagePermissions(false, true, false, true, true);
        await new DynamoAdminStore(db, "admins", "data", "complex")
            .SetPermissionsAsync(Super, Regular, new(Regular.Version, permissions), default);
        var writes = db.Transaction!.TransactItems;
        var actor = Assert.Single(writes, w => w.ConditionCheck is not null).ConditionCheck;
        Assert.Contains("IsSuperAdmin = :yes", actor.ConditionExpression);
        Assert.Equal("4", actor.ExpressionAttributeValues[":version"].N);
        var update = Assert.Single(writes, w => w.Update is not null).Update;
        Assert.Equal("2", update.ExpressionAttributeValues[":expected"].N);
        Assert.DoesNotContain("IsSuperAdmin", JsonSerializer.Serialize(update));
        Assert.True(update.ExpressionAttributeValues[":messages"].BOOL);
        Assert.False(update.ExpressionAttributeValues[":complex"].BOOL);
        Assert.Single(writes, w => w.Put?.Item.GetValueOrDefault("action")?.S == "AdminPermissionsChanged");
    }

    [Fact]
    public async Task MissingPermissionFieldsPreserveLegacyAccessAndSavedFieldsAreRead()
    {
        using var db = new NotificationDynamo();
        db.Administrators["regular"] = Administrator();
        db.Administrators["regular"]["UserId"] = new("regular");
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        Assert.Equal(PagePermissions.Legacy, (await store.GetAdminAsync("regular", default))!.EffectivePermissions);
        db.Administrators["regular"]["AccessComplexRequests"] = new() { BOOL = false };
        db.Administrators["regular"]["AccessMessages"] = new() { BOOL = true };
        Assert.Equal(new PagePermissions(false, true, true, true, true), (await store.GetAdminAsync("regular", default))!.EffectivePermissions);
    }

    [Fact]
    public async Task OwnSettingsCheckActiveInUpdateWithoutDuplicateTransactionItem()
    {
        using var db = new NotificationDynamo();
        await new DynamoAdminStore(db, "admins", "data", "complex").SetNotificationSettingsAsync(Super, Super, Settings() with { Version = Super.Version }, default);
        Assert.DoesNotContain(db.Transaction!.TransactItems, w => w.ConditionCheck is not null);
        var update = Assert.Single(db.Transaction.TransactItems, w => w.Update is not null).Update;
        Assert.Contains("IsActive = :yes", update.ConditionExpression);
        Assert.DoesNotContain("IsSuperAdmin", JsonSerializer.Serialize(update));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("chat")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task InvalidChatCannotEnableNotifications(string? chat)
    {
        using var db = new NotificationDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .SetNotificationSettingsAsync(Super, Regular, Settings(chat), default));
        Assert.Equal(400, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task ClearingChatWithAllSubscriptionsDisabledIsAllowed()
    {
        using var db = new NotificationDynamo();
        await new DynamoAdminStore(db, "admins", "data", "complex").SetNotificationSettingsAsync(Super, Regular, new(null, false, false, false, 2), default);
        Assert.True(Assert.Single(db.Transaction!.TransactItems, w => w.Update is not null).Update.ExpressionAttributeValues[":chat"].NULL);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task InactiveOrNonSuperActorCannotEditOthersSettings(bool active, bool super)
    {
        using var db = new NotificationDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .SetNotificationSettingsAsync(Super with { IsActive = active, IsSuperAdmin = super }, Regular, Settings(), default));
        Assert.Equal(403, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task SettingsConflictIsReportedWithoutIndependentWrites()
    {
        using var db = new NotificationDynamo { Conflict = true };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .SetNotificationSettingsAsync(Super, Regular, Settings(), default));
        Assert.Equal(409, error.Status);
        Assert.Equal(1, db.Transactions);
    }

    [Fact]
    public async Task NewAccountAndBroadcastAreAtomicAndRepeatRegistrationDoesNotCreateAnotherEvent()
    {
        using var db = new NotificationDynamo();
        db.Administrators["new"] = Administrator(active: false);
        db.Administrators["new"]["UserId"] = new("new");
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        await store.RegisterAccountAsync(new("new", "new@example.invalid", "New"), default);
        var writes = db.Transaction!.TransactItems;
        var update = Assert.Single(writes, w => w.Update is not null).Update;
        Assert.DoesNotContain("IsSuperAdmin", update.UpdateExpression);
        Assert.Equal("attribute_not_exists(UserId)", update.ConditionExpression);
        var job = Assert.Single(writes, w => w.Put is not null).Put.Item;
        var notification = JsonSerializer.Deserialize<ResidentNotification>(job["payload"].S)!;
        Assert.Equal(AdministratorEvent.NewAdministrator, notification.AdministratorEvent);
        Assert.Equal("new-administrator-new", notification.Id);
        Assert.True(notification.IsBroadcast);
        Assert.False(job.ContainsKey("expiresAt"));
        db.Conflict = true;
        var existing = await store.RegisterAccountAsync(new("new", "new@example.invalid", "New"), default);
        Assert.Equal("new", existing.UserId);
        Assert.Equal(2, db.Transactions);
    }

    [Theory]
    [InlineData(AdministratorEvent.NewResident)]
    [InlineData(AdministratorEvent.NewComplexRequest)]
    [InlineData(AdministratorEvent.NewKeyOrder)]
    [InlineData(AdministratorEvent.NewAdministrator)]
    public async Task BroadcastExpandsOnlySubscribedActiveRecipientsUsingCurrentBaseRows(AdministratorEvent kind)
    {
        using var db = new NotificationDynamo();
        db.Administrators["active"] = Administrator();
        db.Administrators["inactive"] = Administrator(active: false);
        db.Administrators["disabled"] = Administrator(enabled: false);
        db.Administrators["missing-chat"] = Administrator(chat: null);
        db.Page = new() { Items = db.Administrators.Keys.Select(id => new Dictionary<string, AttributeValue> { ["UserId"] = new(id) }).ToList() };
        await new DynamoNotificationRepository(db, "data", "complex", "admins").ExpandAsync(Broadcast(kind), "lease", Now, default);
        Assert.Equal("DirectoryIndex", db.Query!.IndexName);
        Assert.Equal(25, db.Query.Limit);
        Assert.Equal(4, db.AdminReads);
        var writes = db.Transaction!.TransactItems;
        var job = Assert.Single(writes, w => w.Put?.Item.ContainsKey("payload") == true).Put.Item;
        var notification = JsonSerializer.Deserialize<ResidentNotification>(job["payload"].S)!;
        Assert.Equal(123, notification.OwnerId);
        Assert.Equal("active", notification.AdministratorId);
        Assert.False(notification.IsBroadcast);
        Assert.Equal(kind, notification.AdministratorEvent);
        Assert.Equal("NOTIFICATIONS#complex", job["pk"].S);
        Assert.False(job.ContainsKey("expiresAt"));
        Assert.Equal("lease", Assert.Single(writes, w => w.Delete is not null).Delete.ExpressionAttributeValues[":lease"].S);
        Assert.Equal("Expanded", Assert.Single(writes, w => w.Put?.Item.ContainsKey("disposition") == true).Put.Item["disposition"].S);
    }

    [Theory]
    [InlineData(AdministratorEvent.NewResident)]
    [InlineData(AdministratorEvent.NewComplexRequest)]
    [InlineData(AdministratorEvent.NewKeyOrder)]
    [InlineData(AdministratorEvent.NewAdministrator)]
    public void EachSubscriptionSelectsOnlyItsOwnEvent(AdministratorEvent kind)
    {
        var settings = new AdministratorNotificationSettings("123", kind == AdministratorEvent.NewResident,
            kind == AdministratorEvent.NewComplexRequest, kind == AdministratorEvent.NewAdministrator, kind == AdministratorEvent.NewKeyOrder);
        foreach (var candidate in Enum.GetValues<AdministratorEvent>().Where(candidate => candidate != AdministratorEvent.NewRequest))
            Assert.Equal(candidate == kind, settings.Includes(candidate));
    }

    [Fact]
    public void RequestEventsDistinguishKeyOrdersAndKeepLegacySubscription()
    {
        var request = new MaintenanceRequest(7, 42, "complex", "Інше", "", "Опис", Now);
        Assert.Equal(AdministratorEvent.NewComplexRequest, AdministratorNotifications.NewRequest(request).AdministratorEvent);
        Assert.Equal(AdministratorEvent.NewKeyOrder, AdministratorNotifications.NewRequest(request with { Category = RequestCategories.KeyOrder }).AdministratorEvent);
        var legacy = new AdministratorNotificationSettings("123", NotifyNewRequests: true);
        Assert.True(legacy.Includes(AdministratorEvent.NewRequest));
        Assert.True(legacy.Includes(AdministratorEvent.NewComplexRequest));
        Assert.True(legacy.Includes(AdministratorEvent.NewKeyOrder));
        Assert.False((legacy with { NotifyNewKeyOrders = false }).Includes(AdministratorEvent.NewKeyOrder));
    }

    [Theory]
    [InlineData(true, false, AdministratorEvent.NewComplexRequest, true)]
    [InlineData(true, false, AdministratorEvent.NewKeyOrder, false)]
    [InlineData(false, true, AdministratorEvent.NewComplexRequest, false)]
    [InlineData(false, true, AdministratorEvent.NewKeyOrder, true)]
    public async Task BroadcastAndDeliveryRespectIndependentRequestSubscriptions(bool complex, bool keys, AdministratorEvent kind, bool expected)
    {
        using var db = new NotificationDynamo();
        var admin = Administrator();
        admin["NotifyNewRequests"] = new() { BOOL = complex };
        admin["NotifyNewKeyOrders"] = new() { BOOL = keys };
        db.Administrators["recipient"] = admin;
        db.Page = new() { Items = [new() { ["UserId"] = new("recipient") }] };
        var repository = new DynamoNotificationRepository(db, "data", "complex", "admins");
        var notification = Broadcast(kind);
        await repository.ExpandAsync(notification, "lease", Now, default);
        Assert.Equal(expected ? 1 : 0, db.Transaction!.TransactItems.Count(w => w.Put?.Item.ContainsKey("attempts") == true));
        var direct = notification with { Notification = notification.Notification with { OwnerId = 123, AdministratorId = "recipient" } };
        Assert.Equal(expected, await repository.CanDeliverAsync(direct, default));
    }

    [Fact]
    public async Task KeyOrderSettingRequiresChatAndIsWrittenIndependently()
    {
        using var db = new NotificationDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var change = new NotificationSettingsChange(null, false, false, false, 2, true);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => store.SetNotificationSettingsAsync(Super, Regular, change, default))).Status);
        await store.SetNotificationSettingsAsync(Super, Regular, change with { TelegramChatId = "123" }, default);
        var update = Assert.Single(db.Transaction!.TransactItems, w => w.Update is not null).Update;
        Assert.True(update.ExpressionAttributeValues[":keys"].BOOL);
        Assert.False(update.ExpressionAttributeValues[":requests"].BOOL);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingAdministratorsInheritPreviousRequestSubscription(bool previous)
    {
        using var db = new NotificationDynamo();
        db.Administrators["regular"] = Administrator();
        db.Administrators["regular"]["NotifyNewRequests"] = new() { BOOL = previous };
        var admin = await new DynamoAdminStore(db, "admins", "data", "complex").GetAdminAsync("regular", default);
        Assert.Equal(previous, admin!.NotifyNewRequests);
        Assert.Equal(previous, admin.NotifyNewKeyOrders);
        db.Administrators["regular"]["NotifyNewKeyOrders"] = new() { BOOL = !previous };
        admin = await new DynamoAdminStore(db, "admins", "data", "complex").GetAdminAsync("regular", default);
        Assert.Equal(!previous, admin!.NotifyNewKeyOrders);
    }

    [Fact]
    public async Task OlderClientLeavesIndependentKeyOrderSubscriptionUnchanged()
    {
        using var db = new NotificationDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var target = Regular with { NotifyNewKeyOrders = true };
        await store.SetNotificationSettingsAsync(Super, target, new("123", false, false, false, 2), default);
        var update = Assert.Single(db.Transaction!.TransactItems, w => w.Update is not null).Update;
        Assert.True(update.ExpressionAttributeValues[":keys"].BOOL);
        Assert.False(update.ExpressionAttributeValues[":requests"].BOOL);
    }

    [Fact]
    public async Task BroadcastTraversesMoreThan25RecipientsWithoutDroppingOrRepeatingPages()
    {
        using var db = new NotificationDynamo();
        var candidates = Enumerable.Range(1, 26).Select(index => "admin-" + index).ToList();
        foreach (var id in candidates) db.Administrators[id] = Administrator();
        db.Page = new()
        {
            Items = candidates.Take(25).Select(id => new Dictionary<string, AttributeValue> { ["UserId"] = new(id) }).ToList(),
            LastEvaluatedKey = new() { ["UserId"] = new("admin-25"), ["Directory"] = new("ADMINS"), ["Email"] = new("25@example.invalid") }
        };
        var repository = new DynamoNotificationRepository(db, "data", "complex", "admins");
        await repository.ExpandAsync(Broadcast(), "lease", Now, default);
        var writes = db.Transaction!.TransactItems;
        var ids = writes.Where(write => write.Put is not null).Select(write => JsonSerializer.Deserialize<ResidentNotification>(write.Put.Item["payload"].S)!.Id).ToList();
        Assert.Equal(25, ids.Count);
        var cursor = JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.Single(writes, write => write.Update is not null).Update.ExpressionAttributeValues[":cursor"].S);
        db.Page = new() { Items = [new() { ["UserId"] = new("admin-26") }] };
        await repository.ExpandAsync(Broadcast() with { AudienceCursor = cursor }, "lease-2", Now, default);
        ids.Add(JsonSerializer.Deserialize<ResidentNotification>(Assert.Single(db.Transaction!.TransactItems, write => write.Put?.Item.ContainsKey("payload") == true).Put.Item["payload"].S)!.Id);
        Assert.Equal(26, ids.Distinct().Count());
        Assert.Equal(26, db.AdminReads);
    }

    [Fact]
    public async Task FanoutCheckpointsPageAndKeepsCursorOnRetry()
    {
        using var db = new NotificationDynamo { Page = new() { Items = [], LastEvaluatedKey = new() { ["UserId"] = new("last"), ["Directory"] = new("ADMINS"), ["Email"] = new("last@example.invalid") } } };
        var repository = new DynamoNotificationRepository(db, "data", "complex", "admins");
        await repository.ExpandAsync(Broadcast(), "lease", Now, default);
        var update = Assert.Single(db.Transaction!.TransactItems).Update;
        var cursor = JsonSerializer.Deserialize<Dictionary<string, string>>(update.ExpressionAttributeValues[":cursor"].S)!;
        Assert.Equal("last", cursor["UserId"]);
        Assert.Contains("REMOVE leaseToken, leaseExpiresAt", update.UpdateExpression);
        var next = Broadcast() with { AudienceCursor = cursor };
        db.Page = new() { Items = [] };
        await repository.ExpandAsync(next, "next-lease", Now, default);
        Assert.Equal("last", db.Query!.ExclusiveStartKey["UserId"].S);
        await repository.RetryAsync(next, "next-lease", Now.AddMinutes(1), default);
        var retry = Assert.Single(db.Transaction!.TransactItems, w => w.Put is not null).Put.Item;
        Assert.Equal(cursor, JsonSerializer.Deserialize<Dictionary<string, string>>(retry["audienceCursor"].S));
        Assert.False(retry.ContainsKey("expiresAt"));
    }

    [Theory]
    [InlineData(true, true, "123", true)]
    [InlineData(true, true, "-100123", true)]
    [InlineData(false, true, "123", false)]
    [InlineData(true, false, "123", false)]
    [InlineData(true, true, "456", false)]
    public async Task DeliveryRechecksCurrentActivitySubscriptionAndDestination(bool active, bool enabled, string chat, bool expected)
    {
        using var db = new NotificationDynamo();
        db.Administrators["active"] = Administrator(active, chat, enabled);
        var notification = Broadcast().Notification with { AdministratorId = "active", OwnerId = chat == "-100123" ? -100123 : 123 };
        var item = Broadcast() with { Notification = notification };
        var repository = new DynamoNotificationRepository(db, "data", "complex", "admins");
        Assert.Equal(expected, await repository.CanDeliverAsync(item, default));
        Assert.True(db.LastGet!.ConsistentRead);
        if (expected) Assert.True(await repository.ClaimAsync(item, "lease", Now, default));
    }

    [Fact]
    public async Task ForeignBroadcastCannotReadAdministrators()
    {
        using var db = new NotificationDynamo();
        var item = Broadcast() with { Notification = Broadcast().Notification with { ComplexId = "other" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DynamoNotificationRepository(db, "data", "complex", "admins").ExpandAsync(item, "lease", Now, default));
        Assert.Null(db.Query);
        Assert.Equal(0, db.AdminReads);
    }

    private sealed class NotificationDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public Dictionary<string, Dictionary<string, AttributeValue>> Administrators = [];
        public QueryResponse Page = new() { Items = [] };
        public QueryRequest? Query;
        public GetItemRequest? LastGet;
        public TransactWriteItemsRequest? Transaction;
        public int Transactions;
        public int AdminReads;
        public bool Conflict;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            LastGet = request;
            AdminReads++;
            return Task.FromResult(new GetItemResponse { Item = Administrators.GetValueOrDefault(request.Key["UserId"].S) ?? [] });
        }
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        { Query = request; return Task.FromResult(Page); }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken ct = default) => Task.FromResult(new UpdateItemResponse());
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        { Transaction = request; Transactions++; if (Conflict) throw new TransactionCanceledException("Conflict"); return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
