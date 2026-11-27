using System.Text.Json;
using OsbbRequests.CommandProcessor.Commands;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Residents;
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
        var processor = new TelegramCommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building"), telegram, "building");
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
        var processor = new TelegramCommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building"), new FakeTelegram());
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
        var processor = new TelegramCommandProcessor(repo, new CommandHandler(repo, "OsbbBot", "building", residentApprovalMode: mode), telegram, "building");
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
}
