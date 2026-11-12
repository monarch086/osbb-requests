using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class AutomaticResidentApprovalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static CommandEnvelope Message(long id, string text, TelegramContact? contact = null) => new(id, 99, 99, text, contact);
    private static readonly ResidentRegistration Approved = new(new string('a', 32), 99, "building", RegistrationStage.Approved,
        6, Now.AddMinutes(-1), Now, "+380671234567", "Олена", "Ковальчук", BuildingNumber: "4Д", ApartmentNumber: "125", ApprovedAt: Now, ConsentAt: Now.AddMinutes(-1), ConsentVersion: "1");

    [Fact]
    public async Task DefaultModeGrantsAccessOnlyAfterFinalStepAndSynchronizesResidentMenu()
    {
        var repo = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building"), telegram, "building");
        await processor.ProcessAsync(Message(1, "/start"), default);
        await processor.ProcessAsync(Message(1001, RegistrationService.ConsentButton), default);
        Assert.Contains("автоматично", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 99)), default);
        await processor.ProcessAsync(Message(3, "Олена"), default);
        await processor.ProcessAsync(Message(4, "Ковальчук"), default);
        await processor.ProcessAsync(Message(61, "Член родини"), default);
        await processor.ProcessAsync(Message(5, "Будинок 4Д"), default);
        Assert.DoesNotContain(99, repo.Residents);
        Assert.Equal(RegistrationStage.AwaitingApartment, repo.Registrations[99].Stage);
        await processor.ProcessAsync(Message(6, "125"), default);
        Assert.DoesNotContain(99, repo.Residents);
        var final = Message(60, RegistrationService.SkipEmail);
        await processor.ProcessAsync(final, default);
        Assert.Contains(99, repo.Residents);
        Assert.Equal(RegistrationStage.Approved, repo.Registrations[99].Stage);
        Assert.Equal(repo.Registrations[99].UpdatedAt, repo.Registrations[99].ApprovedAt);
        Assert.Equal((99L, true), telegram.Menus[^1]);
        Assert.DoesNotContain("очікує", telegram.Messages[^1].Text);
        await processor.ProcessAsync(final, default);
        Assert.Equal(9, repo.Registrations[99].Version);
        await processor.ProcessAsync(Message(7, "/new 4Д | 2 | інше | Зламано світло"), default);
        Assert.Single(repo.Requests);
    }

    [Theory]
    [InlineData("Власник", ResidentType.Owner, false)]
    [InlineData("Член родини", ResidentType.FamilyMember, true)]
    [InlineData("Орендар", ResidentType.Tenant, true)]
    public async Task ResidentTypeControlsAutomaticApproval(string choice, ResidentType type, bool automatic)
    {
        var repo = new MemoryRepository();
        repo.Registrations[99] = Approved with { Stage = RegistrationStage.AwaitingResidentType, ApprovedAt = null };
        var processor = new CommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building"), new FakeTelegram());
        await processor.ProcessAsync(Message(1, "некоректно"), default);
        Assert.Equal(RegistrationStage.AwaitingResidentType, repo.Registrations[99].Stage);
        var selection = Message(2, choice);
        await processor.ProcessAsync(selection, default);
        await processor.ProcessAsync(selection, default);
        Assert.Equal(type, repo.Registrations[99].ResidentType);
        Assert.Equal(automatic ? RegistrationStage.Approved : RegistrationStage.Pending, repo.Registrations[99].Stage);
        Assert.Equal(automatic, repo.Residents.Contains(99));
        if (!automatic)
        {
            await processor.ProcessAsync(Message(3, "/new"), default);
            Assert.Empty(repo.Requests);
        }
    }

    [Fact]
    public void LegacyProfilesDefaultToFamilyMember()
    {
        var json = "{\"Id\":\"old\",\"UserId\":99,\"BuildingId\":\"building\",\"Stage\":\"Approved\",\"Version\":1}";
        Assert.Equal(ResidentType.FamilyMember, JsonSerializer.Deserialize<ResidentRegistration>(json)!.ResidentType);
    }

    [Fact]
    public async Task ExistingPendingRegistrationStillRequiresManualReview()
    {
        var repo = new MemoryRepository();
        var pending = Approved with { Stage = RegistrationStage.Pending, ApprovedAt = null };
        repo.Registrations[99] = pending;
        var outcome = await new CommandHandler(repo, "OsbbBot", "building").HandleAsync(Message(1, "/start"), default);
        Assert.Contains("очікує перевірки", outcome.Reply);
        Assert.Null(outcome.Registration);
        Assert.DoesNotContain(99, repo.Residents);
    }

    [Theory]
    [InlineData(ResidentApprovalMode.Automatic)]
    [InlineData(ResidentApprovalMode.Manual)]
    public async Task EmployeeRegistrationSkipsAddressAndRequiresReview(ResidentApprovalMode mode)
    {
        var repo = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building", residentApprovalMode: mode), telegram, "building");
        await processor.ProcessAsync(Message(1, "/start"), default);
        await processor.ProcessAsync(Message(2, RegistrationService.ConsentButton), default);
        await processor.ProcessAsync(Message(3, "", new("+380671234567", 99)), default);
        await processor.ProcessAsync(Message(4, "Олена"), default);
        await processor.ProcessAsync(Message(5, "Ковальчук"), default);
        Assert.Equal(RegistrationStage.AwaitingInitialResidentType, repo.Registrations[99].Stage);
        await processor.ProcessAsync(Message(6, "Працівник ОСББ"), default);
        Assert.Equal(RegistrationStage.AwaitingEmail, repo.Registrations[99].Stage);
        var final = Message(7, RegistrationService.SkipEmail);
        await processor.ProcessAsync(final, default);
        await processor.ProcessAsync(final, default);
        var profile = repo.Registrations[99];
        Assert.Equal(ResidentType.Employee, profile.ResidentType);
        Assert.Equal(RegistrationStage.Pending, profile.Stage);
        Assert.Null(profile.BuildingNumber);
        Assert.Null(profile.ApartmentNumber);
        Assert.NotNull(profile.ConsentAt);
        Assert.DoesNotContain(99, repo.Residents);
        Assert.Contains("адміністратору", telegram.Messages[^1].Text);
        Assert.DoesNotContain("Будинок:", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(8, "/new"), default);
        Assert.Empty(repo.Requests);
        Assert.DoesNotContain(99, repo.Residents);
    }

    [Fact]
    public async Task EmployeePendingRegistrationIsStoredAtomicallyWithoutMembership()
    {
        using var db = new Capture();
        var employee = Approved with { ResidentType = ResidentType.Employee, Stage = RegistrationStage.Pending, ApprovedAt = null, BuildingNumber = null, ApartmentNumber = null };
        await new DynamoCommandRepository(db, "data").CommitAsync(Message(7, RegistrationService.SkipEmail),
            new("На перевірці", Registration: new(employee, 5)), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Single(writes, w => w.Put.Item["pk"].S == "BUILDING#building" && w.Put.Item["sk"].S.StartsWith("REGISTRATION#", StringComparison.Ordinal));
        Assert.Single(writes, w => w.Put.Item["pk"].S == "NOTIFICATIONS#building");
        Assert.DoesNotContain(writes, w => w.Put.Item["sk"].S == "MEMBERSHIP");
        Assert.DoesNotContain(writes, w => w.Put.Item["sk"].S.StartsWith("RESIDENT#", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutomaticApprovalAtomicallyWritesMembershipDirectoryAuditAndAdminNotificationWithoutPendingEntry()
    {
        using var db = new Capture();
        await new DynamoCommandRepository(db, "data").CommitAsync(Message(6, "125"),
            new("Доступ надано", Registration: new(Approved, 5, AutomaticallyApproved: true)), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(7, writes.Count);
        var profile = Assert.Single(writes, w => w.Put.Item["sk"].S == "REGISTRATION").Put;
        Assert.Equal("5", profile.ExpressionAttributeValues[":expected"].N);
        Assert.Single(writes, w => w.Put.Item["sk"].S == "META");
        var directory = Assert.Single(writes, w => w.Put.Item["sk"].S.StartsWith("RESIDENT#", StringComparison.Ordinal)).Put.Item;
        Assert.Equal("BUILDING#building", directory["pk"].S);
        Assert.Equal(RegistrationStage.Approved, JsonSerializer.Deserialize<ResidentRegistration>(directory["payload"].S)!.Stage);
        var membership = Assert.Single(writes, w => w.Put.Item["sk"].S == "MEMBERSHIP").Put;
        Assert.True(membership.Item["active"].BOOL);
        Assert.Equal("building", membership.ExpressionAttributeValues[":complex"].S);
        Assert.Equal("system", membership.Item["reviewedBy"].S);
        var audit = Assert.Single(writes, w => w.Put.Item["sk"].S == "EVENT#DECIDED").Put.Item;
        Assert.Equal("Automatic", audit["approvalMode"].S);
        Assert.Equal("Approved", audit["decision"].S);
        Assert.DoesNotContain(writes, w => w.Put.Item["pk"].S == "BUILDING#building" && w.Put.Item["sk"].S.StartsWith("REGISTRATION#", StringComparison.Ordinal));
        var notification = JsonSerializer.Deserialize<ResidentNotification>(Assert.Single(writes, w => w.Put.Item["pk"].S == "NOTIFICATIONS#building").Put.Item["payload"].S)!;
        Assert.Equal(AdministratorEvent.NewResident, notification.AdministratorEvent);
        Assert.Contains("схвалено автоматично", notification.Text);
        Assert.DoesNotContain("Потребує перевірки", notification.Text);
        Assert.DoesNotContain(Approved.Phone!, notification.Text);
        Assert.All(writes.Where(w => w.Put.Item["sk"].S != "REPLY"), w => Assert.False(w.Put.Item.ContainsKey("expiresAt")));
    }

    [Theory]
    [InlineData(RegistrationStage.Pending)]
    [InlineData(RegistrationStage.Approved)]
    public async Task RegistrationMirrorIsInSameGuardedTransaction(RegistrationStage stage)
    {
        using var db = new Capture();
        var profile = Approved with { Stage = stage, ApprovedAt = stage == RegistrationStage.Approved ? Approved.ApprovedAt : null };
        await new DynamoCommandRepository(db, "data", residentsTable: "residents", complexId: "building").CommitAsync(Message(6, "125"),
            new("ok", Registration: new(profile, 5, AutomaticallyApproved: stage == RegistrationStage.Approved)), default);
        Assert.DoesNotContain(db.Transaction!.TransactItems, w => w.Put?.TableName == "data" && w.Put.Item.GetValueOrDefault("sk")?.S is "REGISTRATION" or "META" or "MEMBERSHIP" or "EVENT#DECIDED");
        var mirror = Assert.Single(db.Transaction!.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item["pk"].S.StartsWith("COMPLEX#building#USER#", StringComparison.Ordinal)).Put;
        Assert.Equal("COMPLEX#building#USER#99", mirror.Item["pk"].S);
        Assert.Equal("COMPLEX#building#" + stage, mirror.Item["directory"].S);
        Assert.Equal("5", mirror.ExpressionAttributeValues[":version"].N);
        Assert.Contains("#version = :version", mirror.ConditionExpression);
        Assert.Equal(profile, JsonSerializer.Deserialize<ResidentRegistration>(mirror.Item["payload"].S));
        Assert.False(mirror.Item.ContainsKey("expiresAt"));
    }

    [Fact]
    public async Task AutomaticApprovalMirrorsMetadataMembershipAndDecisionInSameTransaction()
    {
        using var db = new Capture();
        await new DynamoCommandRepository(db, "data", residentsTable: "residents").CommitAsync(Message(6, "125"),
            new("ok", Registration: new(Approved, 5, AutomaticallyApproved: true)), default);
        var related = db.Transaction!.TransactItems.Where(w => w.Put?.TableName == "residents" && w.Put.Item.ContainsKey("recordData")).Select(w => w.Put).ToArray();
        Assert.Equal(5, related.Length);
        var meta = Assert.Single(related, w => w.Item["sourceSk"].S == "META");
        Assert.Equal(Approved.Id, JsonSerializer.Deserialize<ResidentRegistration>(meta.Item["recordData"].M["payload"].S)!.Id);
        var member = Assert.Single(related, w => w.Item["sourceSk"].S == "MEMBERSHIP");
        Assert.True(member.Item["recordData"].M["active"].BOOL);
        Assert.Equal("system", member.Item["recordData"].M["reviewedBy"].S);
        var audit = Assert.Single(related, w => w.Item["sourceSk"].S == "EVENT#DECIDED");
        Assert.Equal("Approved", audit.Item["recordData"].M["decision"].S);
        Assert.Equal("attribute_not_exists(pk)", audit.ConditionExpression);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("missing-apartment")]
    [InlineData("missing-phone")]
    [InlineData("missing-building")]
    [InlineData("missing-firstname")]
    [InlineData("missing-consent")]
    [InlineData("owner")]
    [InlineData("employee")]
    public async Task IncompleteRegistrationCannotCreateMembership(string kind)
    {
        using var db = new Capture();
        var profile = kind switch
        {
            "pending" => Approved with { Stage = RegistrationStage.Pending },
            "missing-consent" => Approved with { ConsentAt = null },
            "owner" => Approved with { ResidentType = ResidentType.Owner },
            "employee" => Approved with { ResidentType = ResidentType.Employee },
            "missing-apartment" => Approved with { ApartmentNumber = null },
            "missing-phone" => Approved with { Phone = null },
            "missing-building" => Approved with { BuildingNumber = null },
            _ => Approved with { FirstName = null }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DynamoCommandRepository(db, "data").CommitAsync(Message(6, "125"),
            new("Доступ", Registration: new(profile, 5, AutomaticallyApproved: true)), default));
        Assert.Null(db.Transaction);
    }

    [Theory]
    [InlineData(null, ResidentApprovalMode.Automatic)]
    [InlineData("Automatic", ResidentApprovalMode.Automatic)]
    [InlineData("Manual", ResidentApprovalMode.Manual)]
    public void ConfigurationSupportsBothModes(string? value, ResidentApprovalMode expected) => Assert.Equal(expected, ResidentApprovalConfiguration.Parse(value));

    [Theory]
    [InlineData("manual")]
    [InlineData("true")]
    [InlineData("Unknown")]
    public void InvalidConfigurationFailsWithoutGrantingAccess(string value) => Assert.Throws<InvalidOperationException>(() => ResidentApprovalConfiguration.Parse(value));

    [Fact]
    public async Task RegistrationRestartPreservesAnonymityOfRequestsAfterProfileDeletion()
    {
        var repo = new MemoryRepository();
        repo.Registrations[99] = Approved with { PersonalDataDeleted = true, PersonalDataDeletedAt = Now.AddDays(-1) };
        var outcome = await new CommandHandler(repo, "OsbbBot", "building", new FixedClock()).HandleAsync(Message(1, "/register"), default);
        Assert.NotNull(outcome.Registration);
        Assert.False(outcome.Registration.Value.PersonalDataDeleted);
        Assert.True(outcome.Registration.Value.HasDeletedPersonalData);
        Assert.Equal(RegistrationStage.AwaitingConsent, outcome.Registration.Value.Stage);
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now.AddHours(1); }

    private sealed class Capture() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public TransactWriteItemsRequest? Transaction;
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        { Transaction = request; return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
