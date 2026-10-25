using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Bot.Notifications;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class MessageBroadcastTests
{
    private static readonly AdminUser Super = new("super", "head@example.invalid", "Head", true, true, 1);
    private const string Id = "a51503dd-a080-4a9a-9875-26300c6d702d";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static NotificationWorkItem Broadcast() => new("DUE#broadcast", new("message-" + Id, "complex", 0, null, null,
        "Повідомлення мешканцям", Now, ResidentBroadcastId: Id), 0);
    private static Dictionary<string, AttributeValue> Row<T>(T payload) => new() { ["payload"] = new(JsonSerializer.Serialize(payload)) };

    [Fact]
    public async Task CreationAtomicallyPersistsHistoryIdentityOutboxAndCurrentSuperRoleGuard()
    {
        using var db = new FakeDynamo();
        var message = await new DynamoAdminStore(db, "admins", "data", "complex").SendMessageAsync(Super, new(Id, " Текст\nоголошення "), default);
        Assert.Equal("Текст\nоголошення", message.Text);
        Assert.Equal(Super.Email, message.CreatedBy);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(4, writes.Count);
        Assert.Contains("IsSuperAdmin = :yes", Assert.Single(writes, w => w.ConditionCheck is not null).ConditionCheck.ConditionExpression);
        var jobs = writes.Where(w => w.Put is not null).Select(w => w.Put.Item).ToList();
        Assert.All(jobs, item => Assert.False(item.ContainsKey("expiresAt")));
        Assert.Equal(2, jobs.Count(item => item["pk"].S == "MESSAGES#complex"));
        var notification = JsonSerializer.Deserialize<ResidentNotification>(Assert.Single(jobs, item => item["pk"].S == "NOTIFICATIONS#complex")["payload"].S)!;
        Assert.True(notification.IsBroadcast);
        Assert.Equal(message.Text, notification.Text);
        Assert.Equal(Id, notification.ResidentBroadcastId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task StoreRejectsInactiveOrRegularAdministrators(bool active, bool super)
    {
        using var db = new FakeDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .SendMessageAsync(Super with { IsActive = active, IsSuperAdmin = super }, new(Id, "Текст"), default));
        Assert.Equal(403, error.Status);
        Assert.Null(db.Transaction);
    }

    [Theory]
    [InlineData("", "Текст")]
    [InlineData(Id, "  \n ")]
    [InlineData(Id, null)]
    public async Task InvalidMessageCannotBeQueued(string id, string? text)
    {
        using var db = new FakeDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .SendMessageAsync(Super, new(id, text!), default));
        Assert.Equal(400, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task TelegramLengthLimitIsEnforced()
    {
        using var db = new FakeDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        await store.SendMessageAsync(Super, new(Id, new string('я', 4096)), default);
        var error = await Assert.ThrowsAsync<ApiError>(() => store.SendMessageAsync(Super, new(Id, new string('я', 4097)), default));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task RetryReturnsExistingMessageWithoutDuplicatingOutbox()
    {
        using var db = new FakeDynamo();
        var existing = new BroadcastMessage(Id, "Текст", Super.Email, Super.UserId, Now);
        db.Existing = Row(existing);
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        Assert.Equal(existing, await store.SendMessageAsync(Super, new(Id, "Текст"), default));
        Assert.Null(db.Transaction);
        var error = await Assert.ThrowsAsync<ApiError>(() => store.SendMessageAsync(Super, new(Id, "Інше повідомлення"), default));
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public async Task ConcurrentIdenticalSubmissionRecoversFromConditionalConflict()
    {
        using var db = new FakeDynamo { Conflict = true };
        db.AfterConflict = Row(new BroadcastMessage(Id, "Текст", Super.Email, Super.UserId, Now));
        var message = await new DynamoAdminStore(db, "admins", "data", "complex").SendMessageAsync(Super, new(Id, "Текст"), default);
        Assert.Equal(Now, message.CreatedAt);
        Assert.Equal(2, db.LastGet!.Key.Count);
    }

    [Fact]
    public async Task RevokedSuperRoleDuringTransactionDoesNotLeaveHistoryOrOutbox()
    {
        using var db = new FakeDynamo { Conflict = true };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex").SendMessageAsync(Super, new(Id, "Текст"), default));
        Assert.Equal(409, error.Status);
        Assert.Contains("IsSuperAdmin", db.Transaction!.TransactItems[0].ConditionCheck.ConditionExpression);
    }

    [Fact]
    public async Task HistoryUsesDescendingScopedQueryWith25ItemPagesAndValidatedCursor()
    {
        using var db = new FakeDynamo();
        db.Page = new()
        {
            Items = [Row(new BroadcastMessage(Id, "Текст", Super.Email, Super.UserId, Now))],
            LastEvaluatedKey = new() { ["pk"] = new("MESSAGES#complex"), ["sk"] = new("MESSAGE#timestamp#id") }
        };
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var first = await store.ListMessagesAsync(null, default);
        Assert.Single(first.Items);
        Assert.False(db.Query!.ScanIndexForward);
        Assert.True(db.Query.ConsistentRead);
        Assert.Equal(25, db.Query.Limit);
        Assert.Equal("MESSAGES#complex", db.Query.ExpressionAttributeValues[":pk"].S);
        await store.ListMessagesAsync(first.Cursor, default);
        Assert.Equal(db.Page.LastEvaluatedKey["sk"].S, db.Query.ExclusiveStartKey["sk"].S);
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "other").ListMessagesAsync(first.Cursor, default));
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task AudienceExpansionIsPagedAndAtomicAndSkipsForeignPendingAndLaterRegistrations()
    {
        using var db = new FakeDynamo();
        var residents = Enumerable.Range(1, 25).Select(id => Row(new ResidentRegistration("registration" + id, id, "complex",
            RegistrationStage.Approved, 1, Now.AddDays(-1), Now.AddDays(-1)))).ToList();
        db.Page = new() { Items = residents, LastEvaluatedKey = new() { ["pk"] = new("BUILDING#complex"), ["sk"] = new("RESIDENT#25") } };
        var repository = new DynamoNotificationRepository(db, "data", "complex");
        await repository.ExpandAsync(Broadcast(), "lease", Now, default);
        Assert.Equal(25, db.Query!.Limit);
        Assert.True(db.Query.ConsistentRead);
        Assert.Equal("BUILDING#complex", db.Query.ExpressionAttributeValues[":pk"].S);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(25, writes.Count(w => w.Put is not null));
        var continuation = Assert.Single(writes, w => w.Update is not null).Update;
        Assert.Equal("lease", continuation.ExpressionAttributeValues[":lease"].S);
        var cursor = JsonSerializer.Deserialize<Dictionary<string, string>>(continuation.ExpressionAttributeValues[":cursor"].S)!;
        Assert.All(writes.Where(w => w.Put is not null), w =>
        {
            Assert.False(w.Put.Item.ContainsKey("expiresAt"));
            var notification = JsonSerializer.Deserialize<ResidentNotification>(w.Put.Item["payload"].S)!;
            Assert.False(notification.IsBroadcast);
            Assert.InRange(notification.OwnerId, 1, 25);
        });
        db.Page = new()
        {
            Items = [
            Row(new ResidentRegistration("26", 26, "complex", RegistrationStage.Approved, 1, Now.AddDays(-1), Now.AddDays(-1))),
            Row(new ResidentRegistration("27", 27, "other", RegistrationStage.Approved, 1, Now, Now)),
            Row(new ResidentRegistration("28", 28, "complex", RegistrationStage.Pending, 1, Now, Now)),
            Row(new ResidentRegistration("29", 29, "complex", RegistrationStage.Approved, 1, Now, Now.AddSeconds(1)))
        ]
        };
        var next = Broadcast() with { AudienceCursor = cursor };
        await repository.ExpandAsync(next, "lease2", Now, default);
        Assert.Equal("RESIDENT#25", db.Query.ExclusiveStartKey["sk"].S);
        var recipient = Assert.Single(db.Transaction!.TransactItems, w => w.Put?.Item.ContainsKey("payload") == true).Put.Item;
        var job = JsonSerializer.Deserialize<ResidentNotification>(recipient["payload"].S)!;
        Assert.Equal(26, job.OwnerId);
        db.Existing = Row(new ResidentRegistration("26", 26, "complex", RegistrationStage.Approved, 1, Now, Now));
        Assert.True(await repository.CanDeliverAsync(next with { Notification = job }, default));
        Assert.False(await repository.CanDeliverAsync(Broadcast(), default));
        Assert.Single(db.Transaction.TransactItems, w => w.Delete is not null);
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.Item.GetValueOrDefault("disposition")?.S == "Expanded");
        await repository.RetryAsync(next, "lease2", Now.AddMinutes(1), default);
        Assert.Equal(cursor, JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.Single(db.Transaction!.TransactItems, w => w.Put is not null).Put.Item["audienceCursor"].S));
    }

    [Fact]
    public async Task ClaimRejectsStaleAudienceSnapshotAfterAnotherInvocationAdvancesThePage()
    {
        using var db = new FakeDynamo();
        var repository = new DynamoNotificationRepository(db, "data", "complex");
        Assert.True(await repository.ClaimAsync(Broadcast(), "lease", Now, default));
        Assert.Contains("attribute_not_exists(audienceCursor)", db.Claim!.ConditionExpression);
        var cursor = new Dictionary<string, string> { ["pk"] = "BUILDING#complex", ["sk"] = "RESIDENT#25" };
        Assert.True(await repository.ClaimAsync(Broadcast() with { AudienceCursor = cursor }, "lease", Now, default));
        Assert.Contains("audienceCursor = :cursor", db.Claim.ConditionExpression);
        Assert.Equal(cursor, JsonSerializer.Deserialize<Dictionary<string, string>>(db.Claim.ExpressionAttributeValues[":cursor"].S));
        db.StaleClaim = true;
        Assert.False(await repository.ClaimAsync(Broadcast(), "lease", Now, default));
    }

    [Fact]
    public async Task ForeignBroadcastCannotEnumerateResidents()
    {
        using var db = new FakeDynamo();
        var item = Broadcast() with { Notification = Broadcast().Notification with { ComplexId = "other" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DynamoNotificationRepository(db, "data", "complex").ExpandAsync(item, "lease", Now, default));
        Assert.Null(db.Query);
    }

    [Fact]
    public async Task ProfileEditsDoNotExcludePreviouslyApprovedResidentsFromBroadcastAudience()
    {
        using var db = new FakeDynamo();
        db.Page = new()
        {
            Items = [Row(new ResidentRegistration("reference", 42, "complex", RegistrationStage.Approved, 7,
            Now.AddDays(-10), Now.AddHours(1), ApprovedAt: Now.AddDays(-9)))]
        };
        await new DynamoNotificationRepository(db, "data", "complex").ExpandAsync(Broadcast(), "lease", Now.AddHours(2), default);
        var recipient = Assert.Single(db.Transaction!.TransactItems, item => item.Put?.Item.ContainsKey("payload") == true).Put.Item;
        Assert.Equal(42, JsonSerializer.Deserialize<ResidentNotification>(recipient["payload"].S)!.OwnerId);
    }

    [Theory]
    [InlineData(RegistrationStage.Deleted, true, false)]
    [InlineData(RegistrationStage.Approved, true, false)]
    [InlineData(RegistrationStage.Pending, false, false)]
    [InlineData(RegistrationStage.Approved, false, true)]
    public async Task OldResidentNotificationsCannotReactivateDeletedResidentsMenu(RegistrationStage stage, bool deleted, bool allowed)
    {
        using var db = new FakeDynamo
        {
            Existing = Row(new ResidentRegistration("reference", 42, "complex", stage, 7, Now, Now,
            PersonalDataDeleted: deleted))
        };
        var repository = new DynamoNotificationRepository(db, "data", "complex");
        var item = Broadcast() with { Notification = new("registration-reference-v6", "complex", 42, null, null, "Доступ надано", Now) };
        Assert.Equal(allowed, await repository.CanDeliverAsync(item, default));
        Assert.Equal("BUILDING#complex", db.LastGet!.Key["pk"].S);
        Assert.True(db.LastGet.ConsistentRead);
    }

    private sealed class FakeDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public Dictionary<string, AttributeValue>? Existing;
        public Dictionary<string, AttributeValue>? AfterConflict;
        public bool Conflict;
        public GetItemRequest? LastGet;
        public QueryRequest? Query;
        public UpdateItemRequest? Claim;
        public bool StaleClaim;
        public QueryResponse Page = new() { Items = [] };
        public TransactWriteItemsRequest? Transaction;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        { LastGet = request; return Task.FromResult(new GetItemResponse { Item = Existing ?? [] }); }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken ct = default)
        {
            Claim = request;
            if (StaleClaim) throw new ConditionalCheckFailedException("Stale audience");
            return Task.FromResult(new UpdateItemResponse());
        }
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        { Query = request; return Task.FromResult(Page); }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        {
            Transaction = request;
            if (Conflict) { Existing = AfterConflict; throw new TransactionCanceledException("Conflict"); }
            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }
}
