using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Models;
using System.Text.Json;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentProfileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly ResidentRegistration Original = new("reference", 42, "building", RegistrationStage.Approved, 6,
        Now.AddDays(-10), Now.AddDays(-9), "+380500000000", "Олена", "Ковальчук", BuildingNumber: "4Б", ApartmentNumber: "12", Email: "old@example.com", ResidentType: ResidentType.Owner, ConsentAt: Now.AddDays(-10), ConsentVersion: "1");
    private static CommandEnvelope Message(long id, string text, TelegramContact? contact = null) => new(id, 42, 42, text, contact);
    private static (MemoryRepository Repo, CommandProcessor Processor) Setup()
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Original;
        return (repo, new(repo, new CommandHandler(repo, "OsbbBot", "building", new Clock()), new FakeTelegram()));
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task EditingRunsRegistrationStepsAndCommitsOnlyAfterEmailWithoutRevokingAccess()
    {
        var (repo, processor) = Setup();
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        Assert.Equal(Original, repo.Registrations[42]);
        Assert.Equal(ReplyKeyboard.RequestContact, repo.Replies[1].Keyboard);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        await processor.ProcessAsync(Message(5, "Будинок 4Д"), default);
        Assert.Equal(Original, repo.Registrations[42]);
        await processor.ProcessAsync(Message(6, "125"), default);
        Assert.Equal(Original, repo.Registrations[42]);
        await processor.ProcessAsync(Message(7, "new@example.com"), default);
        var profile = repo.Registrations[42];
        Assert.Equal("Іван", profile.FirstName);
        Assert.Equal("Новий", profile.LastName);
        Assert.Equal("4Д", profile.BuildingNumber);
        Assert.Equal("125", profile.ApartmentNumber);
        Assert.Equal("new@example.com", profile.Email);
        Assert.Equal(Original.Id, profile.Id);
        Assert.Equal(Original.ResidentType, profile.ResidentType);
        Assert.Equal(Original.ConsentAt, profile.ConsentAt);
        Assert.Equal(Original.ConsentVersion, profile.ConsentVersion);
        Assert.Equal(RegistrationStage.Approved, profile.Stage);
        Assert.Equal(Original.UpdatedAt, profile.ApprovedAt);
        Assert.Equal(7, profile.Version);
        Assert.Contains(42, repo.Residents);
        Assert.False(repo.ProfileDialogs[42].Active);
        Assert.Null(repo.ProfileDialogs[42].Draft);
        await processor.ProcessAsync(Message(6, "125"), default);
        Assert.Equal(profile, repo.Registrations[42]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditingLegacyDraftPreservesExistingConsentWithoutAskingAgain(bool hasConsent)
    {
        var (repo, processor) = Setup();
        var existing = hasConsent ? Original : Original with { ConsentAt = null, ConsentVersion = null };
        repo.Registrations[42] = existing;
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        var dialog = repo.ProfileDialogs[42];
        Assert.Equal(ReplyKeyboard.RequestContact, repo.Replies[1].Keyboard);
        repo.ProfileDialogs[42] = dialog with { Draft = dialog.Draft! with { ConsentAt = null, ConsentVersion = null } };
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        await processor.ProcessAsync(Message(5, "4Д"), default);
        await processor.ProcessAsync(Message(6, "125"), default);
        await processor.ProcessAsync(Message(7, RegistrationService.SkipEmail), default);
        Assert.Equal(RegistrationStage.Approved, repo.Registrations[42].Stage);
        Assert.Equal(existing.ConsentAt, repo.Registrations[42].ConsentAt);
        Assert.Equal(existing.ConsentVersion, repo.Registrations[42].ConsentVersion);
        Assert.DoesNotContain(repo.Replies.Values, reply => reply.Keyboard == ReplyKeyboard.PersonalDataConsent);
    }

    [Fact]
    public async Task EditingCannotChangeResidentTypeEvenIfDraftContainsAnotherType()
    {
        var (repo, processor) = Setup();
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        var dialog = repo.ProfileDialogs[42];
        repo.ProfileDialogs[42] = dialog with { Draft = dialog.Draft! with { ResidentType = ResidentType.Tenant } };
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        await processor.ProcessAsync(Message(5, "4Д"), default);
        await processor.ProcessAsync(Message(6, "125"), default);
        await processor.ProcessAsync(Message(7, RegistrationService.SkipEmail), default);
        Assert.Equal(ResidentType.Owner, repo.Registrations[42].ResidentType);
        Assert.Equal(RegistrationStage.Approved, repo.Registrations[42].Stage);
    }

    [Fact]
    public async Task EditingProfilePreservesTypeChangeHistoryMarker()
    {
        var (repo, processor) = Setup();
        repo.Registrations[42] = Original with { TypeChangeCount = 2 };
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        await processor.ProcessAsync(Message(5, "4Д"), default);
        await processor.ProcessAsync(Message(6, "125"), default);
        await processor.ProcessAsync(Message(7, RegistrationService.SkipEmail), default);
        Assert.Equal(2, repo.Registrations[42].TypeChangeCount);
    }

    [Fact]
    public async Task EmployeeEditsProfileWithoutAddressOrLosingApprovalAndConsent()
    {
        var (repo, processor) = Setup();
        var employee = Original with { ResidentType = ResidentType.Employee, BuildingNumber = null, ApartmentNumber = null };
        repo.Registrations[42] = employee;
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        Assert.Equal(RegistrationStage.AwaitingEmail, repo.ProfileDialogs[42].Draft!.Stage);
        await processor.ProcessAsync(Message(5, RegistrationService.SkipEmail), default);
        Assert.Equal(ResidentType.Employee, repo.Registrations[42].ResidentType);
        Assert.Equal(RegistrationStage.Approved, repo.Registrations[42].Stage);
        Assert.Equal(employee.ConsentAt, repo.Registrations[42].ConsentAt);
        Assert.Null(repo.Registrations[42].BuildingNumber);
        Assert.Null(repo.Registrations[42].ApartmentNumber);
        Assert.Contains(42, repo.Residents);
    }

    [Theory]
    [InlineData("/edit_profile")]
    [InlineData("/account")]
    [InlineData("/start")]
    [InlineData("/cancel")]
    [InlineData("Орендар")]
    [InlineData("/new")]
    public async Task PendingOwnerCannotChangeTypeOrGainAccessUsingCommands(string command)
    {
        var (repo, processor) = Setup();
        var pending = Original with { Stage = RegistrationStage.Pending, ApprovedAt = null };
        repo.Registrations[42] = pending;
        repo.Residents.Remove(42);
        await processor.ProcessAsync(Message(1, command), default);
        Assert.Equal(pending, repo.Registrations[42]);
        Assert.DoesNotContain(42, repo.Residents);
        Assert.Empty(repo.Requests);
        Assert.Empty(repo.ProfileDialogs);
    }

    [Fact]
    public async Task SkippingEmailDuringEditRemovesPreviousEmail()
    {
        var (repo, processor) = Setup();
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        await processor.ProcessAsync(Message(5, "4Д"), default);
        await processor.ProcessAsync(Message(6, "125"), default);
        Assert.Equal("old@example.com", repo.Registrations[42].Email);
        await processor.ProcessAsync(Message(7, RegistrationService.SkipEmail), default);
        Assert.Null(repo.Registrations[42].Email);
        Assert.Contains(42, repo.Residents);
    }

    [Theory]
    [InlineData("/cancel")]
    [InlineData("Скасувати")]
    public async Task CancellationDiscardsDraftAndKeepsOriginalProfile(string cancel)
    {
        var (repo, processor) = Setup();
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, cancel), default);
        Assert.Equal(Original, repo.Registrations[42]);
        Assert.Null(repo.ProfileDialogs[42].Draft);
        Assert.False(repo.ProfileDialogs[42].Active);
    }

    [Fact]
    public async Task DeletionRequiresExactConfirmationRetainsRequestsAndCannotRepeatOnRedelivery()
    {
        var (repo, processor) = Setup();
        repo.Requests.Add(new(7, 42, "building", "інше", "", "Текст заявки", Now));
        var request = repo.Requests[0];
        await processor.ProcessAsync(Message(1, "/delete_profile"), default);
        Assert.Equal(Original, repo.Registrations[42]);
        Assert.Equal(ReplyKeyboard.ProfileDeleteConfirmation, repo.Replies[1].Keyboard);
        await processor.ProcessAsync(Message(2, "так"), default);
        Assert.Equal(Original, repo.Registrations[42]);
        var confirm = Message(3, ResidentProfileService.Confirmation);
        await processor.ProcessAsync(confirm, default);
        var profile = repo.Registrations[42];
        Assert.True(profile.PersonalDataDeleted);
        Assert.Null(profile.FirstName);
        Assert.Null(profile.LastName);
        Assert.Null(profile.Phone);
        Assert.Null(profile.Email);
        Assert.Null(profile.BuildingNumber);
        Assert.Null(profile.ApartmentNumber);
        Assert.Equal(Now, profile.PersonalDataDeletedAt);
        Assert.Equal(Original.UpdatedAt, profile.ApprovedAt);
        Assert.Equal(request, Assert.Single(repo.Requests));
        Assert.DoesNotContain(42, repo.Residents);
        Assert.Equal(RegistrationStage.Deleted, profile.Stage);
        await processor.ProcessAsync(confirm, default);
        Assert.Equal(profile, repo.Registrations[42]);
    }

    [Theory]
    [InlineData("/edit_profile")]
    [InlineData("/delete_profile")]
    public async Task UnapprovedResidentsCannotModifyOrDeleteProfiles(string command)
    {
        var (repo, processor) = Setup();
        repo.Residents.Clear();
        await processor.ProcessAsync(Message(1, command), default);
        Assert.Empty(repo.ProfileDialogs);
        Assert.Equal(Original, repo.Registrations[42]);
    }

    [Fact]
    public async Task ConcurrentProfileChangeCancelsDialogWithoutOverwritingNewVersion()
    {
        var (repo, processor) = Setup();
        await processor.ProcessAsync(Message(1, "/delete_profile"), default);
        repo.Registrations[42] = Original with { Version = 7, FirstName = "Змінено" };
        await processor.ProcessAsync(Message(2, ResidentProfileService.Confirmation), default);
        Assert.Equal("Змінено", repo.Registrations[42].FirstName);
        Assert.False(repo.Registrations[42].PersonalDataDeleted);
        Assert.False(repo.ProfileDialogs[42].Active);
    }

    [Fact]
    public async Task OtherCommandsClearProfileDialogWithoutApplyingDraft()
    {
        var (repo, processor) = Setup();
        await processor.ProcessAsync(Message(1, "/edit_profile"), default);
        await processor.ProcessAsync(Message(2, "/my"), default);
        Assert.False(repo.ProfileDialogs[42].Active);
        Assert.Equal(Original, repo.Registrations[42]);
    }

    [Fact]
    public async Task RepositoryUpdatesAllCurrentProfileCopiesAndClearsDialogAtomicallyWithReceiptAndMembershipGuard()
    {
        using var db = new CaptureDynamo();
        var dialog = new ResidentProfileDialog(42, "building", ProfileAction.Delete, 2, 6, null, false);
        var deleted = Original with
        {
            FirstName = null,
            LastName = null,
            Phone = null,
            Email = null,
            BuildingNumber = null,
            ApartmentNumber = null,
            PersonalDataDeleted = true,
            Stage = RegistrationStage.Deleted,
            PersonalDataDeletedAt = Now,
            Version = 7
        };
        await new DynamoCommandRepository(db, "data").CommitAsync(Message(1, ResidentProfileService.Confirmation),
            new("Видалено", ProfileDialog: new(dialog, 1), Profile: new(deleted, 6)), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(6, writes.Count);
        var guard = Assert.Single(writes, w => w.Update is not null).Update;
        Assert.Equal("MEMBERSHIP", guard.Key["sk"].S);
        Assert.Contains("#active", guard.ConditionExpression);
        Assert.False(guard.ExpressionAttributeValues[":no"].BOOL);
        var profiles = writes.Where(w => w.Put?.Item.GetValueOrDefault("sk")?.S is "REGISTRATION" or "META" ||
            w.Put?.Item.GetValueOrDefault("sk")?.S?.StartsWith("RESIDENT#", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(3, profiles.Count);
        foreach (var write in profiles)
        {
            var profile = JsonSerializer.Deserialize<ResidentRegistration>(write.Put.Item["payload"].S)!;
            Assert.True(profile.PersonalDataDeleted);
            Assert.Null(profile.FirstName);
            Assert.Null(profile.Phone);
            Assert.Null(profile.Email);
            Assert.Equal("6", write.Put.ExpressionAttributeValues[":expected"].N);
        }
        Assert.DoesNotContain(writes, w => w.Delete is not null);
        Assert.DoesNotContain(writes, w => w.Put?.Item.ContainsKey("requestBuildingId") == true);
    }
    [Fact]
    public async Task RepeatRegistrationMakesCurrentProfileAvailableForExistingRequests()
    {
        var (repo, processor) = Setup();
        repo.Registrations[42] = Original with
        {
            Phone = null,
            Email = null,
            FirstName = null,
            LastName = null,
            BuildingNumber = null,
            ApartmentNumber = null,
            PersonalDataDeleted = true,
            Stage = RegistrationStage.Deleted,
            PersonalDataDeletedAt = Now.AddDays(-1)
        };
        repo.Residents.Remove(42);
        await processor.ProcessAsync(Message(1, "/start"), default);
        await processor.ProcessAsync(Message(1001, RegistrationService.ConsentButton), default);
        await processor.ProcessAsync(Message(2, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(3, "Іван"), default);
        await processor.ProcessAsync(Message(4, "Новий"), default);
        await processor.ProcessAsync(Message(1007, "Член родини"), default);
        await processor.ProcessAsync(Message(5, "4Д"), default);
        await processor.ProcessAsync(Message(6, "125"), default);
        await processor.ProcessAsync(Message(7, RegistrationService.SkipEmail), default);
        var profile = repo.Registrations[42];
        Assert.False(profile.PersonalDataDeleted);
        Assert.Equal("Іван", profile.FirstName);
        Assert.Equal(Now, profile.PersonalDataDeletedAt);
        Assert.False(profile.HasDeletedPersonalData);
    }

    [Fact]
    public async Task ProfileDialogFromAnotherComplexCannotBeResumedOrCancelled()
    {
        var (repo, _) = Setup();
        repo.ProfileDialogs[42] = new(42, "other", ProfileAction.Delete, 1, 6, null);
        var service = new ResidentProfileService(repo, "building", new Clock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(42, ProfileAction.Delete, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(42, default));
    }

    [Fact]
    public async Task DeletingProfileAtomicallyRemovesPersonalDataFromMirrorAndIndexes()
    {
        using var db = new CaptureDynamo();
        var state = new ResidentProfileDialog(42, "building", ProfileAction.Delete, 2, 6, null, false);
        var deleted = new ResidentRegistration(Original.Id, 42, "building", RegistrationStage.Deleted, 7, Original.CreatedAt, Original.UpdatedAt, PersonalDataDeleted: true);
        await new DynamoCommandRepository(db, "data", residentsTable: "residents").CommitAsync(Message(1, "confirm"),
            new("Видалено", ProfileDialog: new(state, 1), Profile: new(deleted, 6)), default);
        var mirror = Assert.Single(db.Transaction!.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item["pk"].S.StartsWith("COMPLEX#building#USER#", StringComparison.Ordinal)).Put;
        var mirrored = System.Text.Json.JsonSerializer.Deserialize<ResidentRegistration>(mirror.Item["payload"].S)!;
        Assert.Null(mirrored.FirstName);
        Assert.Null(mirrored.LastName);
        Assert.Null(mirrored.Phone);
        Assert.Null(mirrored.Email);
        Assert.False(mirror.Item.ContainsKey("directory"));
        Assert.Equal("6", mirror.ExpressionAttributeValues[":version"].N);
        Assert.Contains(db.Transaction.TransactItems, w => w.Update?.Key["sk"].S == "MEMBERSHIP");
        var membership = Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "MEMBERSHIP").Put.Item["recordData"].M;
        Assert.False(membership["active"].BOOL);
        Assert.Equal("admin@example.org", membership["reviewedBy"].S);
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "META");
        Assert.Single(db.Transaction.TransactItems, w => w.Put?.TableName == "residents" && w.Put.Item.GetValueOrDefault("sourceSk")?.S == "PROFILE_DIALOG");
    }

    [Fact]
    public async Task ProfileUpdateCannotUseAnotherResidentsIdentity()
    {
        using var db = new CaptureDynamo();
        var state = new ResidentProfileDialog(42, "building", ProfileAction.Delete, 2, 6, null, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DynamoCommandRepository(db, "data").CommitAsync(Message(1, "confirm"),
            new("Видалено", ProfileDialog: new(state, 1), Profile: new(Original with { UserId = 99 }, 6)), default));
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task DeletionReturnsGuestMenuAndBlocksRequestsUntilFullRegistrationCompletes()
    {
        var (repo, _) = Setup();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building", new Clock()), telegram, "building");
        repo.Requests.Add(new(7, 42, "building", "інше", "", "Текст заявки", Now.AddHours(-1)));
        await processor.ProcessAsync(Message(1, "/delete_profile"), default);
        await processor.ProcessAsync(Message(2, ResidentProfileService.Confirmation), default);
        Assert.Equal((42L, false), telegram.Menus[^1]);
        Assert.True(repo.Replies[2].RefreshMenu);
        Assert.DoesNotContain(42, repo.Residents);
        await processor.ProcessAsync(Message(3, "/my"), default);
        Assert.Contains("реєстрацію заново", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(4, "/edit_profile"), default);
        Assert.Contains("реєстрацію заново", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(5, "/start"), default);
        Assert.Equal(RegistrationStage.AwaitingConsent, repo.Registrations[42].Stage);
        Assert.Null(repo.Registrations[42].Phone);
        Assert.Null(repo.Registrations[42].FirstName);
        await processor.ProcessAsync(Message(1001, RegistrationService.ConsentButton), default);
        await processor.ProcessAsync(Message(6, "", new("+380671234567", 42)), default);
        await processor.ProcessAsync(Message(7, "Іван"), default);
        await processor.ProcessAsync(Message(8, "Новий"), default);
        await processor.ProcessAsync(Message(1011, "Член родини"), default);
        await processor.ProcessAsync(Message(9, "4Д"), default);
        Assert.DoesNotContain(42, repo.Residents);
        await processor.ProcessAsync(Message(10, "125"), default);
        Assert.DoesNotContain(42, repo.Residents);
        await processor.ProcessAsync(Message(11, RegistrationService.SkipEmail), default);
        Assert.Contains(42, repo.Residents);
        Assert.Equal((42L, true), telegram.Menus[^1]);
        Assert.Single(repo.Requests);
        Assert.False(repo.Registrations[42].HasDeletedPersonalData);
    }

    private sealed class CaptureDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public TransactWriteItemsRequest? Transaction;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default) =>
            Task.FromResult(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new("USER#42"),
                    ["sk"] = new("MEMBERSHIP"),
                    ["buildingId"] = new("building"),
                    ["active"] = new() { BOOL = true },
                    ["reviewedBy"] = new("admin@example.org")
                }
            });
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        { Transaction = request; return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
