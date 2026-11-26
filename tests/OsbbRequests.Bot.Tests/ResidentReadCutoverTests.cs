using OsbbRequests.Services;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Notifications;
using OsbbRequests.Aws;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Shared.Residents;
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
    public async Task KeyOrderNamesUseCurrentProfilesAndDoNotTrustStalePhoneIndex()
    {
        using var db = new ReadDynamo();
        const string phone = "+380671234567";
        var lookup = OsbbRequests.Shared.Residents.ResidentPhoneIndex.LookupKey("complex", phone);
        foreach (var id in new[] { 42L, 43L, 44L })
        {
            var key = OsbbRequests.Shared.Residents.ResidentPhoneIndex.ItemKey("complex", id, phone);
            db.QueriedItems.Add(new() { ["pk"] = new(key) });
            db.RawRows[key] = new() { ["complexId"] = new("complex"), ["phoneLookup"] = new(lookup), ["residentId"] = new() { N = id.ToString() } };
            var resident = new ResidentRegistration("ref", id, "complex", RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, Phone: phone, FirstName: "Ім’я", LastName: id.ToString(), PersonalDataDeleted: id == 43,
                BuildingNumber: id == 44 ? null : "4В", ApartmentNumber: id == 44 ? null : "25");
            db.Rows[$"COMPLEX#complex#RECORD#USER#{id}#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(resident)) };
        }
        var request = new MaintenanceRequest(1, 99, "complex", RequestCategories.KeyOrder, "", "", DateTimeOffset.UnixEpoch,
            KeyItems: [new("Магніт", phone), new("Перепустка", phone)]);
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var result = await store.GetKeyResidentsAsync(request, default);
        Assert.Equal(2, result[0].Count);
        Assert.Equal("Ім’я 42", result[0][0].Name);
        Assert.Equal("4В", result[0][0].BuildingNumber);
        Assert.Equal("25", result[0][0].ApartmentNumber);
        Assert.Equal("Ім’я 44", result[0][1].Name);
        Assert.Null(result[0][1].BuildingNumber);
        Assert.Null(result[0][1].ApartmentNumber);
        Assert.Equal(result[0], result[1]);
        Assert.Single(db.Queries);
        Assert.All(db.Reads, read => Assert.True(read.ConsistentRead));
    }

    [Fact]
    public async Task KeyResidentDirectoryPagesPhoneGroupsWithCurrentAddressAndEveryKey()
    {
        using var db = new ReadDynamo();
        const string phone = "+380671230025";
        var keys = Enumerable.Range(0, 26).Select(i => new KeyOrderItem("Магніт", $"+38067123{i:D4}", $"serial-{i:D2}")).ToArray();
        var ticket = new MaintenanceRequest(2, 99, "complex", RequestCategories.KeyOrder, "", "", DateTimeOffset.UnixEpoch, Status: OsbbRequests.Domain.RequestStatus.Completed, KeyItems: keys);
        var ticketRow = new Dictionary<string, AttributeValue> { ["payload"] = new(JsonSerializer.Serialize(ticket)) };
        var olderTicket = ticket with { Id = 1, OwnerId = 98, KeyItems = [new("Перепустка", "067 123 0025")] };
        var olderRow = new Dictionary<string, AttributeValue> { ["payload"] = new(JsonSerializer.Serialize(olderTicket)) };
        db.ItemsByIndex["BuildingRequestsIndex"] = [olderRow, ticketRow];
        db.RawRows["USER#99"] = ticketRow;
        db.RawRows["USER#98"] = olderRow;
        var key = OsbbRequests.Shared.Residents.ResidentPhoneIndex.ItemKey("complex", 42, phone);
        var lookup = OsbbRequests.Shared.Residents.ResidentPhoneIndex.LookupKey("complex", phone);
        db.ItemsByIndex["ResidentsByPhone"] = [new() { ["pk"] = new(key) }];
        db.RawRows[key] = new() { ["complexId"] = new("complex"), ["phoneLookup"] = new(lookup), ["residentId"] = new() { N = "42" } };
        var resident = new ResidentRegistration("ref", 42, "complex", RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, Phone: phone, FirstName: "Ірина", LastName: "Тестова", BuildingNumber: "4Г", ApartmentNumber: "25");
        db.Rows["COMPLEX#complex#RECORD#USER#42#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(resident)) };
        var store = new DynamoAdminStore(db, "admins", "old", "complex", prioritizeRequests: false, residentsTable: "residents");
        var page = await store.ListResidentsWithKeysAsync(2, default);
        Assert.Equal(26, page.Total);
        Assert.Equal(2, page.PageNumber);
        var row = Assert.Single(page.Items);
        Assert.Equal(phone, row.Phone);
        var match = Assert.Single(row.Residents);
        Assert.Equal("Ірина Тестова", match.Name);
        Assert.Equal("4Г", match.BuildingNumber);
        Assert.Equal("25", match.ApartmentNumber);
        Assert.Collection(row.Keys, first =>
        {
            Assert.Equal("Магніт", first.Type);
            Assert.Equal("serial-25", first.SerialNumber);
        }, second =>
        {
            Assert.Equal("Перепустка", second.Type);
            Assert.Null(second.SerialNumber);
        });
        var phoneQuery = Assert.Single(db.Queries, q => q.IndexName == "ResidentsByPhone");
        Assert.Equal(lookup, phoneQuery.ExpressionAttributeValues[":phone"].S);
        db.Rows["COMPLEX#complex#RECORD#USER#42#REGISTRATION"]["payload"] = new(JsonSerializer.Serialize(resident with { PersonalDataDeleted = true }));
        var missing = await store.ListResidentsWithKeysAsync(2, default);
        Assert.Equal(26, missing.Total);
        var unmatched = Assert.Single(missing.Items);
        Assert.Empty(unmatched.Residents);
        Assert.Equal(2, unmatched.Keys.Count);
    }

    [Fact]
    public async Task KeyResidentDirectoryReadsOnlyRequestsOnSelectedPage()
    {
        using var db = new ReadDynamo();
        var entries = new List<Dictionary<string, AttributeValue>>();
        for (var id = 1; id <= 26; id++)
        {
            var ticket = new MaintenanceRequest(id, id, "complex", RequestCategories.KeyOrder, "", "", DateTimeOffset.UnixEpoch,
                Status: OsbbRequests.Domain.RequestStatus.Completed, KeyItems: [new("Магніт", $"+38067123{id:D4}")]);
            var row = new Dictionary<string, AttributeValue> { ["payload"] = new(JsonSerializer.Serialize(ticket)) };
            entries.Add(row);
            db.RawRows[$"USER#{id}"] = row;
        }
        db.ItemsByIndex["BuildingRequestsIndex"] = entries;
        db.ItemsByIndex["ResidentsByPhone"] = [];
        var store = new DynamoAdminStore(db, "admins", "old", "complex", prioritizeRequests: false, residentsTable: "residents");
        var page = await store.ListResidentsWithKeysAsync(2, default);
        Assert.Equal(26, page.Total);
        Assert.Equal(1, Assert.Single(Assert.Single(page.Items).Keys).RequestId);
        Assert.Single(db.Reads, read => read.TableName == "old");
        Assert.Single(db.Queries, query => query.IndexName == "ResidentsByPhone");
    }

    [Fact]
    public async Task KeyResidentDirectoryIncludesOnlyCompletedAndRechecksCurrentStatus()
    {
        using var db = new ReadDynamo();
        var rows = new List<Dictionary<string, AttributeValue>>();
        var statuses = Enum.GetValues<OsbbRequests.Domain.RequestStatus>();
        foreach (var status in statuses)
        {
            var id = (int)status + 1;
            var ticket = new MaintenanceRequest(id, id, "complex", RequestCategories.KeyOrder, "", "", DateTimeOffset.UnixEpoch,
                Status: status, KeyItems: [new("Магніт", "+380671234567")]);
            var row = new Dictionary<string, AttributeValue> { ["payload"] = new(JsonSerializer.Serialize(ticket)) };
            rows.Add(row);
            db.RawRows[$"USER#{id}"] = row;
        }
        db.ItemsByIndex["BuildingRequestsIndex"] = rows;
        db.ItemsByIndex["ResidentsByPhone"] = [];
        var store = new DynamoAdminStore(db, "admins", "old", "complex", prioritizeRequests: false, residentsTable: "residents");
        var result = await store.ListResidentsWithKeysAsync(1, default);
        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
        var query = Assert.Single(db.Queries, query => query.IndexName == "BuildingRequestsIndex");
        Assert.Equal("Completed", query.ExpressionAttributeValues[":status0"].S);
        var completedId = (int)OsbbRequests.Domain.RequestStatus.Completed + 1;
        var changed = new MaintenanceRequest(completedId, completedId, "complex", RequestCategories.KeyOrder, "", "", DateTimeOffset.UnixEpoch,
            Status: OsbbRequests.Domain.RequestStatus.InProgress, KeyItems: [new("Магніт", "+380671234567")]);
        db.RawRows[$"USER#{completedId}"] = new() { ["payload"] = new(JsonSerializer.Serialize(changed)) };
        var stale = await store.ListResidentsWithKeysAsync(1, default);
        Assert.Empty(stale.Items);
        Assert.Equal(1, stale.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyChangeUpdatesTheRequestAndWritesHistoryForEveryMatchingResident(bool remove)
    {
        using var db = new ReadDynamo();
        const string phone = "+380671234567";
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var actor = new AdminUser("admin", "admin@example.test", "Admin", true, false, 3, Permissions: new(false, true, false, false, false));
        var request = new MaintenanceRequest(1, 99, "complex", RequestCategories.KeyOrder, "", "Замовлення", DateTimeOffset.UnixEpoch,
            Status: RequestStatus.Completed, KeyItems: [new("Магніт", phone, "old"), new("Магніт", phone, "other")]);
        db.RawRows["USER#99"] = new() { ["payload"] = new(JsonSerializer.Serialize(request)), ["version"] = new() { N = "4" } };
        db.ItemsByIndex[ResidentPhoneIndex.IndexName] = [];
        foreach (var id in new[] { 42, 43 })
        {
            var key = ResidentPhoneIndex.ItemKey("complex", id, phone);
            db.ItemsByIndex[ResidentPhoneIndex.IndexName].Add(new() { ["pk"] = new(key) });
            db.RawRows[key] = new() { ["complexId"] = new("complex"), ["phoneLookup"] = new(ResidentPhoneIndex.LookupKey("complex", phone)), ["residentId"] = new() { N = id.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            var resident = new ResidentRegistration($"ref-{id}", id, "complex", RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: phone, FirstName: "Мешканець");
            db.Rows[$"COMPLEX#complex#RECORD#USER#{id}#REGISTRATION"] = new() { ["payload"] = new(JsonSerializer.Serialize(resident)) };
        }

        var change = remove ? new ResidentKeyUpdate(4, Remove: true) : new ResidentKeyUpdate(4, Type: "Перепустка", SerialNumber: " new ");
        await store.UpdateResidentKeyAsync(actor, 99, 1, 0, change, default);
        var writes = db.Transaction!.TransactItems;
        var update = Assert.Single(writes, item => item.Update is not null).Update;
        Assert.Equal("4", update.ExpressionAttributeValues[":expected"].N);
        var saved = JsonSerializer.Deserialize<MaintenanceRequest>(update.ExpressionAttributeValues[":payload"].S)!;
        Assert.Equal(RequestStatus.Completed, saved.Status);
        Assert.Equal(remove ? 1 : 2, saved.KeyQuantity);
        Assert.Equal("other", saved.KeyItems![^1].SerialNumber);
        if (!remove) Assert.Equal(new KeyOrderItem("Перепустка", phone, "new"), saved.KeyItems![0]);

        var history = writes.Where(item => item.Put?.TableName == "residents").Select(item => item.Put.Item).ToArray();
        Assert.Equal(2, writes.Count(item => item.ConditionCheck?.TableName == "residents"));
        Assert.Equal(new[] { "REGISTRATION#ref-42", "REGISTRATION#ref-43" }, history.Select(item => item["sourcePk"].S));
        Assert.All(history, item =>
        {
            Assert.StartsWith("EVENT#TYPE#", item["recordOrder"].S);
            Assert.True(item.ContainsKey(ResidentRecordKey.IndexGroupAttribute));
            var entry = JsonSerializer.Deserialize<ResidentTypeChange>(item["recordData"].M["payload"].S)!;
            Assert.Equal(actor.Email, entry.Actor);
            Assert.Equal(new ResidentKeyChange(1, new("Магніт", "old"), remove ? null : new("Перепустка", "new")), entry.KeyChange);
        });

        db.Transaction = null;
        var conflict = await Assert.ThrowsAsync<AdminOperationException>(() => store.UpdateResidentKeyAsync(actor, 99, 1, 0, change with { Version = 3 }, default));
        Assert.Equal(AdminFailure.Conflict, conflict.Failure);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task KeyOrderAuthorizationUsesNewTableInAtomicTransaction()
    {
        using var db = new ReadDynamo();
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        var ticket = new MaintenanceRequest(1, 42, "complex", RequestCategories.KeyOrder, "", "Замовлення", DateTimeOffset.UnixEpoch,
            Scope: OsbbRequests.Domain.RequestScope.Complex, KeyType: "Магніт", KeyQuantity: 1);
        await store.CommitAsync(new CommandEnvelope(1, 42, 42, "/new"), new CommandOutcome("ok", ticket, OwnerProfileVersion: 3), default);
        var checks = db.Transaction!.TransactItems.Where(item => item.ConditionCheck is not null).Select(item => item.ConditionCheck).ToList();
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check => Assert.Equal("residents", check.TableName));
        Assert.Contains(checks, check => check.Key["pk"].S.EndsWith("#MEMBERSHIP", StringComparison.Ordinal) && check.ConditionExpression.Contains("recordData.#active", StringComparison.Ordinal));
        Assert.Contains(checks, check => check.Key["pk"].S.EndsWith("#REGISTRATION", StringComparison.Ordinal) && check.ExpressionAttributeValues[":version"].N == "3");
    }

    [Fact]
    public async Task DuplicatePhonesCannotBePersistedInNewKeyOrder()
    {
        using var db = new ReadDynamo();
        var store = new DynamoCommandRepository(db, "old", residentsTable: "residents", complexId: "complex");
        var ticket = new MaintenanceRequest(1, 42, "complex", RequestCategories.KeyOrder, "", "Замовлення", DateTimeOffset.UnixEpoch,
            Visibility: RequestVisibility.Private, KeyQuantity: 2, KeyItems: [new("Магніт", "+380671234567"), new("Перепустка", "0671234567")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(new CommandEnvelope(1, 42, 42, "/new"),
            new CommandOutcome("ok", ticket, OwnerProfileVersion: 3), default));
        Assert.Null(db.Transaction);
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
        public Dictionary<string, List<Dictionary<string, AttributeValue>>> ItemsByIndex { get; } = [];
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        { Queries.Add(request); return Task.FromResult(new QueryResponse { Items = request.IndexName is { } index && ItemsByIndex.TryGetValue(index, out var items) ? items : QueriedItems }); }
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
