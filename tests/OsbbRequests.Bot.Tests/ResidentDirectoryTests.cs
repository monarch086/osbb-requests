using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentDirectoryTests
{
    private static readonly ResidentRegistration Registration = new(new string('a', 32), 99, "building",
        RegistrationStage.Pending, 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        "+380500000000", "Олена", "Тестова", BuildingNumber: "4В", ApartmentNumber: "125", Email: "resident@example.com", ConsentAt: DateTimeOffset.UnixEpoch, ConsentVersion: "1");
    private static readonly AdminUser Actor = new("admin", "admin@example.org", "Admin", true, false, 1);
    private static Dictionary<string, AttributeValue> Item(ResidentRegistration registration) => new()
    {
        ["payload"] = new(JsonSerializer.Serialize(registration)),
        ["version"] = new() { N = registration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };
    private static string Cursor(string building, string sk) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new Dictionary<string, string> { ["pk"] = "BUILDING#" + building, ["sk"] = sk })));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyApprovalAddsResidentToDirectoryInSameGuardedTransaction(bool approved)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration) };
        await new DynamoAdminStore(db, "admins", "data", "building").DecideAsync(Actor, Registration.Id,
            new(approved, approved ? null : "Уточніть дані", 4), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Contains(writes, w => w.ConditionCheck?.TableName == "admins");
        Assert.Contains(writes, w => w.Delete?.Key["sk"].S == "REGISTRATION#" + Registration.Id);
        Assert.Equal(2, writes.Count(w => w.Update?.ConditionExpression.Contains(":previous", StringComparison.Ordinal) == true));
        var directory = writes.SingleOrDefault(w => w.Put?.Item.GetValueOrDefault("sk")?.S.StartsWith("RESIDENT#", StringComparison.Ordinal) == true);
        var pending = writes.Where(w => w.Put?.Item.GetValueOrDefault("pk")?.S == "NOTIFICATIONS#building").ToList();
        if (!approved) { Assert.Null(directory); Assert.Empty(pending); return; }
        var job = Assert.Single(pending).Put;
        var notification = JsonSerializer.Deserialize<ResidentNotification>(job.Item["payload"].S)!;
        Assert.Equal(99, notification.OwnerId);
        Assert.Equal("building", notification.ComplexId);
        Assert.Equal("registration-" + Registration.Id + "-v5", notification.Id);
        Assert.Null(notification.RequestId);
        Assert.Contains("схвалено", notification.Text);
        Assert.Contains("/new", notification.Text);
        Assert.DoesNotContain(Registration.Phone!, notification.Text);
        Assert.DoesNotContain(Registration.LastName!, notification.Text);
        Assert.DoesNotContain(Actor.Email, notification.Text);
        Assert.StartsWith("DUE#", job.Item["sk"].S);
        Assert.Equal("0", job.Item["attempts"].N);
        Assert.Equal("attribute_not_exists(pk)", job.ConditionExpression);
        Assert.False(job.Item.ContainsKey("expiresAt"));
        Assert.False(job.Item.ContainsKey("requestBuildingId"));
        Assert.NotNull(directory);
        Assert.Equal("BUILDING#building", directory.Put.Item["pk"].S);
        Assert.Equal("RESIDENT#00000000000000000099", directory.Put.Item["sk"].S);
        var resident = JsonSerializer.Deserialize<ResidentRegistration>(directory.Put.Item["payload"].S)!;
        Assert.Equal(RegistrationStage.Approved, resident.Stage);
        Assert.Equal(Registration.FirstName, resident.FirstName);
        Assert.Equal(Registration.BuildingNumber, resident.BuildingNumber);
        Assert.Equal(Registration.ApartmentNumber, resident.ApartmentNumber);
        Assert.Equal(Registration.Email, resident.Email);
        Assert.Equal(Registration.ConsentAt, resident.ConsentAt);
        Assert.Equal(5, resident.Version);
        Assert.False(directory.Put.Item.ContainsKey("expiresAt"));
    }

    [Theory]
    [InlineData(RegistrationStage.Approved, 4, "building", 409)]
    [InlineData(RegistrationStage.Pending, 3, "building", 409)]
    [InlineData(RegistrationStage.Pending, 4, "other", 404)]
    public async Task InvalidApprovalDoesNotQueueNotification(RegistrationStage stage, int version, string complex, int status)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration with { Stage = stage, Version = version, BuildingId = complex }) };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .DecideAsync(Actor, Registration.Id, new(true, null, 4), default));
        Assert.Equal(status, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task ApprovalConflictHasNoSeparateNotificationWrite()
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration), Conflict = true };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .DecideAsync(Actor, Registration.Id, new(true, null, 4), default));
        Assert.Equal(409, error.Status);
        Assert.Equal(1, db.TransactionCalls);
        Assert.Single(db.Transaction!.TransactItems, w => w.Put?.Item.GetValueOrDefault("pk")?.S == "NOTIFICATIONS#building");
    }

    [Theory]
    [InlineData("admin@example.org", "Approved", false)]
    [InlineData("system", "Approved", true)]
    public async Task ApprovalReadsAuditByRegistrationWithConsistentRead(string actor, string decision, bool automatic)
    {
        using var db = new RecordingDynamo { Snapshot = new() { ["actor"] = new(actor), ["decision"] = new(decision), ["createdAt"] = new("2026-01-01T00:00:00+00:00") } };
        var result = await new DynamoAdminStore(db, "admins", "data", "building").GetRegistrationApprovalAsync(Registration.Id, default);
        Assert.Equal(actor, result!.Actor);
        Assert.Equal(automatic, result.Automatic);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), result.CreatedAt);
        Assert.True(db.Get!.ConsistentRead);
        Assert.Equal("REGISTRATION#" + Registration.Id, db.Get.Key["pk"].S);
        Assert.Equal("EVENT#DECIDED", db.Get.Key["sk"].S);
    }

    [Fact]
    public async Task MissingOrRejectedDecisionDoesNotInventApprover()
    {
        using var db = new RecordingDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "building");
        Assert.Null(await store.GetRegistrationApprovalAsync(Registration.Id, default));
        db.Snapshot = new() { ["decision"] = new("Rejected"), ["actor"] = new("admin@example.org") };
        Assert.Null(await store.GetRegistrationApprovalAsync(Registration.Id, default));
    }

    [Fact]
    public async Task ResidentTypeChangeIsAtomicAuditedAndDoesNotGrantAccess()
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration) };
        await new DynamoAdminStore(db, "admins", "data", "building")
            .ChangeResidentTypeAsync(Actor, Registration.Id, new("Owner", 4), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Single(writes, w => w.ConditionCheck?.TableName == "admins");
        var updates = writes.Where(w => w.Update is not null).Select(w => w.Update).ToArray();
        Assert.Equal(3, updates.Length);
        Assert.All(updates, update =>
        {
            Assert.Contains(":previous", update.ConditionExpression);
            Assert.Equal("4", update.ExpressionAttributeValues[":expected"].N);
            var profile = JsonSerializer.Deserialize<ResidentRegistration>(update.ExpressionAttributeValues[":next"].S)!;
            Assert.Equal(RegistrationStage.Pending, profile.Stage);
            Assert.Equal(ResidentType.Owner, profile.ResidentType);
            Assert.Equal(1, profile.TypeChangeCount);
            Assert.Equal(5, profile.Version);
            Assert.Equal(Registration.ConsentAt, profile.ConsentAt);
        });
        var audit = Assert.Single(writes, w => w.Put?.Item.GetValueOrDefault("sk")?.S.StartsWith("EVENT#TYPE#", StringComparison.Ordinal) == true).Put.Item;
        var entry = JsonSerializer.Deserialize<ResidentTypeChange>(audit["payload"].S)!;
        Assert.Equal(Registration.ResidentType.ToString(), entry.PreviousType);
        Assert.Equal("Owner", entry.NewType);
        Assert.Equal(Actor.Email, entry.Actor);
        Assert.DoesNotContain(writes, w => w.Put?.Item.GetValueOrDefault("sk")?.S == "MEMBERSHIP");
    }

    [Theory]
    [InlineData(RegistrationStage.Pending, "REGISTRATION#aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(RegistrationStage.Approved, "RESIDENT#00000000000000000099")]
    public async Task ResidentDataChangeUpdatesAllProjectionsAndAuditsWithoutCopyingNames(RegistrationStage stage, string listKey)
    {
        var current = Registration with { Stage = stage, ResidentType = ResidentType.Owner };
        using var db = new RecordingDynamo { Snapshot = Item(current) };
        await new DynamoAdminStore(db, "admins", "data", "building").UpdateResidentDataAsync(Actor, current.Id, new(" Марія ", " Нова ", "Tenant", 4), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Single(writes, w => w.ConditionCheck?.TableName == "admins");
        var updates = writes.Where(w => w.Update is not null).Select(w => w.Update).ToArray();
        Assert.Equal(3, updates.Length);
        Assert.Contains(updates, u => u.Key["pk"].S == "BUILDING#building" && u.Key["sk"].S == listKey);
        Assert.All(updates, update =>
        {
            Assert.Equal("4", update.ExpressionAttributeValues[":expected"].N);
            var changed = JsonSerializer.Deserialize<ResidentRegistration>(update.ExpressionAttributeValues[":next"].S)!;
            Assert.Equal("Марія", changed.FirstName);
            Assert.Equal("Нова", changed.LastName);
            Assert.Equal(ResidentType.Tenant, changed.ResidentType);
            Assert.Equal(stage, changed.Stage);
            Assert.Equal(current.ConsentAt, changed.ConsentAt);
            Assert.Equal(current.ConsentVersion, changed.ConsentVersion);
            Assert.Equal(5, changed.Version);
            Assert.Equal(1, changed.TypeChangeCount);
        });
        var payload = Assert.Single(writes, w => w.Put?.Item.GetValueOrDefault("sk")?.S.StartsWith("EVENT#TYPE#", StringComparison.Ordinal) == true).Put.Item["payload"].S;
        var audit = JsonSerializer.Deserialize<ResidentTypeChange>(payload)!;
        Assert.True(audit.FirstNameChanged);
        Assert.True(audit.LastNameChanged);
        Assert.Equal("Owner", audit.PreviousType);
        Assert.Equal("Tenant", audit.NewType);
        Assert.DoesNotContain("Марія", payload);
        Assert.DoesNotContain("Нова", payload);
        Assert.DoesNotContain(writes, w => w.Put?.Item.GetValueOrDefault("sk")?.S == "MEMBERSHIP");
    }

    [Theory]
    [InlineData(RegistrationStage.Pending)]
    [InlineData(RegistrationStage.Approved)]
    public async Task AdminEditingAndApprovalAlsoUpdateMirrorAtomically(RegistrationStage stage)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration with { Stage = stage }) };
        var store = new DynamoAdminStore(db, "admins", "data", "building", residentsTable: "residents");
        await store.UpdateResidentDataAsync(Actor, Registration.Id, new("Марія", "Нова", "Owner", 4), default);
        var mirror = Assert.Single(db.Transaction!.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item["pk"].S.StartsWith("COMPLEX#building#USER#", StringComparison.Ordinal)).Put;
        Assert.Equal("4", mirror.ExpressionAttributeValues[":version"].N);
        Assert.Equal("COMPLEX#building#" + stage, mirror.Item["directory"].S);
        Assert.Equal("Марія", JsonSerializer.Deserialize<ResidentRegistration>(mirror.Item["payload"].S)!.FirstName);
        Assert.DoesNotContain(db.Transaction.TransactItems, w => w.Update?.TableName == "data" || w.Put?.TableName == "data");
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "META");
        var audit = Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S.StartsWith("EVENT#TYPE#", StringComparison.Ordinal) == true).Put;
        Assert.Equal("attribute_not_exists(pk)", audit.ConditionExpression);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdminDecisionMirrorsApprovalAndRejectionAtomically(bool approved)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration) };
        var store = new DynamoAdminStore(db, "admins", "data", "building", residentsTable: "residents");
        await store.DecideAsync(Actor, Registration.Id, new(approved, approved ? null : "Причина", 4), default);
        var mirror = Assert.Single(db.Transaction!.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item["pk"].S.StartsWith("COMPLEX#building#USER#", StringComparison.Ordinal)).Put;
        Assert.Equal("4", mirror.ExpressionAttributeValues[":version"].N);
        Assert.Equal(approved, mirror.Item.ContainsKey("directory"));
        Assert.DoesNotContain(db.Transaction.TransactItems, w => w.Update?.TableName == "data" || w.Delete?.TableName == "data");
        var delete = Assert.Single(db.Transaction.TransactItems, w => w.Delete?.TableName == "residents").Delete;
        Assert.Contains("#v = :expected", delete.ConditionExpression);
        Assert.Equal("4", delete.ExpressionAttributeValues[":expected"].N);
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "REGISTRATION");
        Assert.Equal(approved ? 1 : 0, db.Transaction.TransactItems.Count(w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S.StartsWith("RESIDENT#", StringComparison.Ordinal) == true));
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "META");
        var membership = Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "MEMBERSHIP").Put.Item["recordData"].M;
        Assert.Equal(approved, membership["active"].BOOL);
        Assert.Equal(Actor.Email, membership["reviewedBy"].S);
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "EVENT#DECIDED");
    }

    [Fact]
    public async Task MirrorVersionConflictCancelsEntireAdminMutation()
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration), Conflict = true };
        var store = new DynamoAdminStore(db, "admins", "data", "building", residentsTable: "residents");
        var error = await Assert.ThrowsAsync<ApiError>(() => store.UpdateResidentDataAsync(Actor, Registration.Id, new("Марія", "Нова", "Owner", 4), default));
        Assert.Equal(409, error.Status);
        Assert.Equal(1, db.TransactionCalls);
        Assert.Single(db.Transaction!.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item["pk"].S.StartsWith("COMPLEX#building#USER#", StringComparison.Ordinal));
        Assert.DoesNotContain(db.Transaction.TransactItems, w => w.Update?.TableName == "data");
    }

    [Theory]
    [InlineData("", "Тестова", "Owner", 4, RegistrationStage.Pending, 400)]
    [InlineData("Ім'я1", "Тестова", "Owner", 4, RegistrationStage.Pending, 400)]
    [InlineData("Олена", "Тестова", "FamilyMember", 4, RegistrationStage.Pending, 400)]
    [InlineData("Олена", "Тестова", "Owner", 3, RegistrationStage.Approved, 409)]
    [InlineData("Олена", "Тестова", "Owner", 4, RegistrationStage.Deleted, 409)]
    public async Task InvalidResidentDataChangeDoesNotWrite(string first, string last, string type, int version, RegistrationStage stage, int status)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration with { Stage = stage }) };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building").UpdateResidentDataAsync(Actor, Registration.Id, new(first, last, type, version), default));
        Assert.Equal(status, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task ResidentDataChangeRequiresPendingPermissionAndDetectsConflicts()
    {
        var actor = Actor with { Permissions = new PagePermissions(true, true, true, false, false) };
        using var forbidden = new RecordingDynamo { Snapshot = Item(Registration with { Stage = RegistrationStage.Approved }) };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(forbidden, "admins", "data", "building").UpdateResidentDataAsync(actor, Registration.Id, new("Олена", "Нова", "Owner", 4), default));
        Assert.Equal(403, error.Status);
        Assert.Null(forbidden.Transaction);
        using var conflict = new RecordingDynamo { Snapshot = Item(Registration), Conflict = true };
        error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(conflict, "admins", "data", "building").UpdateResidentDataAsync(Actor, Registration.Id, new("Олена", "Нова", "Owner", 4), default));
        Assert.Equal(409, error.Status);
    }

    [Theory]
    [InlineData("Wrong", 4, RegistrationStage.Pending, "building", 400)]
    [InlineData("Owner", 3, RegistrationStage.Pending, "building", 409)]
    [InlineData("Owner", 4, RegistrationStage.Approved, "building", 409)]
    [InlineData("Owner", 4, RegistrationStage.Pending, "other", 404)]
    public async Task InvalidTypeChangeDoesNotWrite(string type, int version, RegistrationStage stage, string complex, int status)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration with { Stage = stage, BuildingId = complex }) };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .ChangeResidentTypeAsync(Actor, Registration.Id, new(type, version), default));
        Assert.Equal(status, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task TypeHistoryUsesScopedConsistentQueryAndRejectsForeignCursor()
    {
        using var db = new RecordingDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "building");
        await store.RegistrationHistoryAsync(Registration.Id, null, default);
        Assert.True(db.Query!.ConsistentRead);
        Assert.Equal("REGISTRATION#" + Registration.Id, db.Query.ExpressionAttributeValues[":pk"].S);
        Assert.Equal("EVENT#TYPE#", db.Query.ExpressionAttributeValues[":prefix"].S);
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["pk"] = "REGISTRATION#other", ["sk"] = "EVENT#TYPE#x" })));
        await Assert.ThrowsAsync<ApiError>(() => store.RegistrationHistoryAsync(Registration.Id, cursor, default));
    }

    [Fact]
    public async Task ResidentTypeChangeRejectsMissingPermissionAndConcurrentUpdates()
    {
        using var forbidden = new RecordingDynamo { Snapshot = Item(Registration) };
        var actor = Actor with { Permissions = new PagePermissions(true, true, true, false, false) };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(forbidden, "admins", "data", "building")
            .ChangeResidentTypeAsync(actor, Registration.Id, new("Employee", 4), default));
        Assert.Equal(403, error.Status);
        Assert.Null(forbidden.Transaction);
        using var concurrent = new RecordingDynamo { Snapshot = Item(Registration), Conflict = true };
        error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(concurrent, "admins", "data", "building")
            .ChangeResidentTypeAsync(Actor, Registration.Id, new("Employee", 4), default));
        Assert.Equal(409, error.Status);
        Assert.Equal(1, concurrent.TransactionCalls);
    }

    [Fact]
    public async Task DirectoryUsesScopedConsistentQueryAndPaginatesWithoutExposingInternalIds()
    {
        using var db = new RecordingDynamo();
        db.Result = new()
        {
            Items = [Item(Registration with { Stage = RegistrationStage.Approved, TypeChangeCount = 2 }),
                Item(Registration with { BuildingId = "other", Stage = RegistrationStage.Approved }), Item(Registration)],
            LastEvaluatedKey = new() { ["pk"] = new("BUILDING#building"), ["sk"] = new("RESIDENT#00000000000000000099") }
        };
        var store = new DynamoAdminStore(db, "admins", "data", "building");
        var result = await store.ListResidentsAsync(Cursor("building", "RESIDENT#00000000000000000001"), default);
        Assert.True(db.Query!.ConsistentRead);
        Assert.Equal(25, db.Query.Limit);
        Assert.Equal("BUILDING#building", db.Query.ExpressionAttributeValues[":pk"].S);
        Assert.Equal("RESIDENT#", db.Query.ExpressionAttributeValues[":prefix"].S);
        Assert.Equal("RESIDENT#00000000000000000001", db.Query.ExclusiveStartKey["sk"].S);
        Assert.Single(result.Items);
        Assert.Equal(Registration.Phone, result.Items[0].Phone);
        Assert.Equal("99", result.Items[0].TelegramChatId);
        Assert.Equal("4В", result.Items[0].BuildingNumber);
        Assert.Equal("125", result.Items[0].ApartmentNumber);
        Assert.Equal("resident@example.com", result.Items[0].Email);
        Assert.Equal(Registration.ConsentAt, result.Items[0].ConsentAt);
        Assert.Equal(2, result.Items[0].TypeChangeCount);
        Assert.Equal(4, result.Items[0].Version);
        Assert.NotNull(result.Cursor);
        await store.ListResidentsAsync(result.Cursor, default);
        Assert.Equal("RESIDENT#00000000000000000099", db.Query.ExclusiveStartKey["sk"].S);
    }

    [Theory]
    [InlineData("other", "RESIDENT#1")]
    [InlineData("building", "REGISTRATION#1")]
    public async Task ForeignBuildingAndOtherListCursorsAreRejected(string building, string sk)
    {
        using var db = new RecordingDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .ListResidentsAsync(Cursor(building, sk), default));
        Assert.Equal(400, error.Status);
        Assert.Null(db.Query);
    }

    [Fact]
    public async Task RequestBuildingAndFloorFiltersRemainQueriesWithinTheComplex()
    {
        using var db = new RecordingDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex", prioritizeRequests: false);
        await store.ListRequestsAsync(new(null, null, null, null, null, "Building4В", 25), null, default);
        Assert.Equal("complex", db.Query!.ExpressionAttributeValues[":building"].S);
        Assert.Equal("Building4В", db.Query.ExpressionAttributeValues[":target"].S);
        Assert.Equal("25", db.Query.ExpressionAttributeValues[":floor"].N);
        Assert.Contains("requestTarget = :target", db.Query.FilterExpression);
        Assert.Contains("requestFloor = :floor", db.Query.FilterExpression);
        await store.ListRequestsAsync(new(null, null, null, null, null, "Unspecified"), null, default);
        Assert.Contains("attribute_not_exists(requestTarget)", db.Query.FilterExpression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Completed")]
    public async Task StatusFilterDoesNotImplicitlyHideCompletedRequests(string? status)
    {
        using var db = new RecordingDynamo();
        db.Result = new()
        {
            Items = [],
            LastEvaluatedKey = new()
            {
                ["pk"] = new("USER#1"),
                ["sk"] = new("REQUEST#1"),
                ["requestBuildingId"] = new("complex"),
                ["requestCreatedAt"] = new("date")
            }
        };
        var store = new DynamoAdminStore(db, "admins", "data", "complex", prioritizeRequests: false);
        var page = await store.ListRequestsAsync(new(status is null ? [] : [status], "електрика", null, null, null, "Building4Б", 25), null, default);
        Assert.Equal("BuildingRequestsIndex", db.Query!.IndexName);
        Assert.Contains("requestCategory = :requestCategory", db.Query.FilterExpression);
        Assert.DoesNotContain(":completed", db.Query.FilterExpression);
        if (status is not null)
        {
            Assert.Contains("requestStatus IN (:status0)", db.Query.FilterExpression);
            Assert.Equal(status, db.Query.ExpressionAttributeValues[":status0"].S);
        }
        else Assert.DoesNotContain("requestStatus", db.Query.FilterExpression);
        Assert.Empty(page.Items);
        Assert.NotNull(page.Cursor);
        await store.ListRequestsAsync(new(null, null, null, null, null), page.Cursor, default);
        Assert.Equal("REQUEST#1", db.Query.ExclusiveStartKey["sk"].S);
    }
    private sealed class RecordingDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public Dictionary<string, AttributeValue>? Snapshot;
        public QueryRequest? Query;
        public GetItemRequest? Get;
        public QueryResponse Result = new() { Items = [] };
        public TransactWriteItemsRequest? Transaction;
        public int TransactionCalls;
        public bool Conflict;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Get = request;
            var item = request.TableName == "residents" && Snapshot is not null
                ? new Dictionary<string, AttributeValue> { ["recordData"] = new() { M = Snapshot } } : Snapshot;
            return Task.FromResult(new GetItemResponse { Item = item });
        }
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        { Query = request; return Task.FromResult(Result); }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        { Transaction = request; TransactionCalls++; if (Conflict) throw new TransactionCanceledException("Conflict"); return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
