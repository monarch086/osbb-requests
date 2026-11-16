using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Notifications;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentReadCutoverTests
{
    [Fact]
    public async Task ProfileAndMembershipUseStronglyConsistentNewRecordsWithComplexIsolation()
    {
        var profile = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using var db = new ReadDynamo();
        db.Rows["COMPLEX#complex#RECORD#USER#42#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(profile)) };
        db.Rows["COMPLEX#complex#RECORD#USER#42#MEMBERSHIP"] = new() { ["buildingId"] = new("complex"), ["active"] = new() { BOOL = true } };
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        Assert.Equal(profile, await store.GetRegistrationAsync(42, default));
        Assert.True(await store.IsResidentAsync(42, "complex", default));
        Assert.False(await store.IsResidentAsync(42, "other", default));
        Assert.All(db.Reads, read => { Assert.True(read.ConsistentRead); Assert.Equal("residents", read.TableName); });
        db.Rows["COMPLEX#complex#RECORD#USER#42#MEMBERSHIP"]["active"] = new() { BOOL = false };
        Assert.False(await store.IsResidentAsync(42, "complex", default));
    }

    [Fact]
    public async Task PhoneLookupUsesApprovedIndexAndRechecksCurrentProfile()
    {
        var profile = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: "+380671234567", ResidentType: ResidentType.Employee);
        using var db = new ReadDynamo();
        db.QueriedItems.Add(new() { ["payload"] = new(JsonSerializer.Serialize(profile)) });
        var recordKey = "COMPLEX#complex#RECORD#USER#42#REGISTRATION";
        db.Rows[recordKey] = new() { ["payload"] = new(JsonSerializer.Serialize(profile)) };
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        Assert.True(await store.ResidentPhoneExistsAsync("complex", "+380671234567", default));
        Assert.Equal("ResidentsByDate", Assert.Single(db.Queries).IndexName);
        Assert.Equal("COMPLEX#complex#Approved", db.Queries[0].ExpressionAttributeValues[":directory"].S);
        Assert.True(Assert.Single(db.Reads).ConsistentRead);
        db.Rows[recordKey]["payload"] = new(JsonSerializer.Serialize(profile with { PersonalDataDeleted = true }));
        Assert.False(await store.ResidentPhoneExistsAsync("complex", "+380671234567", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResidentPhoneExistsAsync("another", "+380671234567", default));
    }

    [Fact]
    public async Task PhoneIndexChecksCurrentAssociationsAndProfilesForSharedNumbers()
    {
        using var db = new ReadDynamo();
        var phone = "+380671234567";
        var first = OsbbRequests.Shared.Residents.ResidentPhoneIndex.ItemKey("complex", 42, phone);
        var second = OsbbRequests.Shared.Residents.ResidentPhoneIndex.ItemKey("complex", 43, phone);
        var lookup = OsbbRequests.Shared.Residents.ResidentPhoneIndex.LookupKey("complex", phone);
        db.QueriedItems.Add(new() { ["pk"] = new(first) });
        db.QueriedItems.Add(new() { ["pk"] = new(second) });
        db.RawRows[second] = new()
        {
            ["pk"] = new(second),
            ["phoneLookup"] = new(lookup),
            ["complexId"] = new("complex"),
            ["residentId"] = new() { N = "43" }
        };
        var profile = new ResidentRegistration("ref", 43, "complex", RegistrationStage.Approved, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: phone, ResidentType: ResidentType.Tenant);
        db.Rows["COMPLEX#complex#RECORD#USER#43#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(profile)) };
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex", usePhoneIndex: true);
        Assert.True(await store.ResidentPhoneExistsAsync("complex", "0671234567", default));
        Assert.Equal("ResidentsByPhone", Assert.Single(db.Queries).IndexName);
        Assert.All(db.Reads, read => Assert.True(read.ConsistentRead));
        db.Rows["COMPLEX#complex#RECORD#USER#43#REGISTRATION"]["payload"] = new(JsonSerializer.Serialize(profile with { Phone = "+380931234567" }));
        Assert.False(await store.ResidentPhoneExistsAsync("complex", phone, default));
    }

    [Fact]
    public async Task KeyOrderAuthorizationUsesNewTableInAtomicTransaction()
    {
        using var db = new ReadDynamo();
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        var ticket = new MaintenanceRequest(1, 42, "complex", RequestCategories.KeyOrder, "", "Замовлення", DateTimeOffset.UnixEpoch,
            Scope: OsbbRequests.Shared.Domain.RequestScope.Complex, KeyType: "Магніт", KeyQuantity: 1);
        await store.CommitAsync(new CommandEnvelope(1, 42, 42, "/new"), new CommandOutcome("ok", ticket, OwnerProfileVersion: 3), default);
        var checks = db.Transaction!.TransactItems.Where(item => item.ConditionCheck is not null).Select(item => item.ConditionCheck).ToList();
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check => Assert.Equal("residents", check.TableName));
        Assert.Contains(checks, check => check.Key["pk"].S.EndsWith("#MEMBERSHIP", StringComparison.Ordinal) && check.ConditionExpression.Contains("recordData.#active", StringComparison.Ordinal));
        Assert.Contains(checks, check => check.Key["pk"].S.EndsWith("#REGISTRATION", StringComparison.Ordinal) && check.ExpressionAttributeValues[":version"].N == "3");
    }

    [Fact]
    public async Task StructuredKeyOrderWithoutLocationKeepsOwnerAuthorizationInTransaction()
    {
        using var db = new ReadDynamo();
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        var ticket = new MaintenanceRequest(1, 42, "complex", RequestCategories.KeyOrder, "", "Магніт", DateTimeOffset.UnixEpoch,
            KeyQuantity: 1, KeyItems: [new("Магніт", "+380671234567")]);
        await store.CommitAsync(new CommandEnvelope(1, 42, 42, "finish"), new CommandOutcome("ok", ticket, OwnerProfileVersion: 3), default);
        var checks = db.Transaction!.TransactItems.Where(item => item.ConditionCheck is not null).Select(item => item.ConditionCheck).ToArray();
        Assert.Equal(2, checks.Length);
        Assert.All(checks, check => Assert.Equal("residents", check.TableName));
        Assert.Contains(db.Transaction.TransactItems, write => write.Put?.TableName == "old" && write.Put.Item.GetValueOrDefault("requestCategory")?.S == RequestCategories.KeyOrder);
    }

    [Fact]
    public async Task DeletingProfileUsesOnlyNewResidentRecordsAndKeepsMembershipGuard()
    {
        using var db = new ReadDynamo();
        db.Rows["COMPLEX#complex#RECORD#USER#42#MEMBERSHIP"] = new()
        {
            ["pk"] = new("USER#42"),
            ["sk"] = new("MEMBERSHIP"),
            ["buildingId"] = new("complex"),
            ["active"] = new() { BOOL = true }
        };
        var previous = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: "+380671234567");
        db.Rows["COMPLEX#complex#RECORD#USER#42#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(previous)) };
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        var dialog = new ResidentProfileDialog(42, "complex", ProfileAction.Delete, 2, 1, null, false);
        var profile = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Deleted, 2,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, PersonalDataDeleted: true);
        await store.CommitAsync(new CommandEnvelope(1, 42, 42, "confirm"),
            new CommandOutcome("ok", ProfileDialog: new(dialog, 1), Profile: new(profile, 1)), default);
        var writes = db.Transaction!.TransactItems;
        Assert.DoesNotContain(writes, w => w.Update?.TableName == "old");
        Assert.DoesNotContain(writes, w => w.Put?.TableName == "old" && w.Put.Item.GetValueOrDefault("sk")?.S is "REGISTRATION" or "META" or "PROFILE_DIALOG");
        var member = Assert.Single(writes, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "MEMBERSHIP").Put;
        Assert.Contains("recordData.#active = :yes", member.ConditionExpression);
        Assert.False(member.Item["recordData"].M["active"].BOOL);
        Assert.Single(writes, w => w.Delete?.TableName == "residents" && w.Delete.Key["pk"].S.Contains("#PHONE_ENTRY#", StringComparison.Ordinal));
        Assert.All(writes.Where(w => w.Put?.TableName == "residents" && w.Put.ExpressionAttributeValues?.ContainsKey(":expected") == true),
            w => Assert.DoesNotContain("attribute_not_exists(pk)", w.Put.ConditionExpression));
    }

    [Theory]
    [InlineData("+380931234567", true)]
    [InlineData("+380671234567", false)]
    public async Task EditingPhoneUpdatesAssociationAtomically(string nextPhone, bool changed)
    {
        using var db = new ReadDynamo();
        var previous = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: "+380671234567");
        db.Rows["COMPLEX#complex#RECORD#USER#42#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(previous)) };
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        var dialog = new ResidentProfileDialog(42, "complex", ProfileAction.Edit, 2, 1, null, false);
        await store.CommitAsync(new CommandEnvelope(1, 42, 42, "confirm"),
            new CommandOutcome("ok", ProfileDialog: new(dialog, 1), Profile: new(previous with { Phone = nextPhone, Version = 2 }, 1)), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Single(writes, w => w.Put?.Item.GetValueOrDefault("phoneLookup")?.S ==
            OsbbRequests.Shared.Residents.ResidentPhoneIndex.LookupKey("complex", nextPhone));
        Assert.Equal(changed ? 1 : 0, writes.Count(w => w.Delete?.Key.GetValueOrDefault("pk")?.S?.Contains("#PHONE_ENTRY#", StringComparison.Ordinal) == true));
        Assert.Contains(writes, w => w.Put?.Item.GetValueOrDefault("sourceSk")?.S == "REGISTRATION");
    }

    [Fact]
    public async Task NotificationDeliveryChecksCurrentResidentInNewTable()
    {
        using var db = new ReadDynamo();
        var profile = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var key = "COMPLEX#complex#RECORD#BUILDING#complex#RESIDENT#00000000000000000042";
        db.Rows[key] = new() { ["payload"] = new(JsonSerializer.Serialize(profile)) };
        var repository = new DynamoNotificationRepository(db, "old", "complex", "admins", "residents");
        var notification = new ResidentNotification("id", "complex", 42, 1, 1, "text", DateTimeOffset.UnixEpoch);
        var work = new NotificationWorkItem("DUE#id", notification, 0);
        Assert.True(await repository.CanDeliverAsync(work, default));
        Assert.True(db.Reads[0].ConsistentRead);
        Assert.Equal("residents", db.Reads[0].TableName);
        db.Rows[key]["payload"] = new(JsonSerializer.Serialize(profile with { PersonalDataDeleted = true }));
        Assert.False(await repository.CanDeliverAsync(work, default));
    }

    [Fact]
    public async Task RequestAuthorUsesCurrentProfileFromNewTable()
    {
        using var db = new ReadDynamo();
        var profile = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, FirstName: "Олена", LastName: "Тестова", ApartmentNumber: "125");
        db.Rows["COMPLEX#complex#RECORD#USER#42#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(profile)) };
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var request = new MaintenanceRequest(1, 42, "complex", "Інше", "", "Опис", DateTimeOffset.UnixEpoch);
        var author = await store.GetRequestAuthorAsync(request, default);
        Assert.Equal("125", author!.ApartmentNumber);
        Assert.Equal("residents", Assert.Single(db.Reads).TableName);
        Assert.True(db.Reads[0].ConsistentRead);
    }

    private sealed class ReadDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public Dictionary<string, Dictionary<string, AttributeValue>> Rows { get; } = [];
        public Dictionary<string, Dictionary<string, AttributeValue>> RawRows { get; } = [];
        public List<GetItemRequest> Reads { get; } = [];
        public List<QueryRequest> Queries { get; } = [];
        public List<Dictionary<string, AttributeValue>> QueriedItems { get; } = [];
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        { Queries.Add(request); return Task.FromResult(new QueryResponse { Items = QueriedItems }); }
        public TransactWriteItemsRequest? Transaction;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Reads.Add(request);
            if (RawRows.TryGetValue(request.Key["pk"].S, out var raw)) return Task.FromResult(new GetItemResponse { Item = raw });
            var item = Rows.GetValueOrDefault(request.Key["pk"].S);
            return Task.FromResult(new GetItemResponse { Item = item is null ? [] : new() { ["recordData"] = new() { M = item } } });
        }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        { Transaction = request; return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
