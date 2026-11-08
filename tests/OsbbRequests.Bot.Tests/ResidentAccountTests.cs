using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentAccountTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly ResidentRegistration Profile = new("reference", 42, "building", RegistrationStage.Approved, 6,
        Now, Now, "+380500000000", "Олена", "Ковальчук", BuildingNumber: "4Б", ApartmentNumber: "1", ResidentType: ResidentType.Owner, ConsentAt: Now, ConsentVersion: "1");
    private static CommandEnvelope Message(long id, string text) => new(id, 42, 42, text);
    private static (MemoryRepository Repo, CommandProcessor Processor) Setup(ResidentRegistration profile)
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = profile;
        return (repo, new(repo, new CommandHandler(repo, "OsbbBot", "building"), new FakeTelegram()));
    }

    [Theory]
    [InlineData("4Б", "1", "0002194б001")]
    [InlineData("4В", "1", "0002194в001")]
    [InlineData("4Г", "12", "0002194г012")]
    [InlineData("4Д", "999", "0002194д999")]
    public async Task CompleteProfileProducesAccountWithoutWritingProfile(string building, string apartment, string expected)
    {
        var original = Profile with { BuildingNumber = building, ApartmentNumber = apartment };
        var (repo, processor) = Setup(original);
        await processor.ProcessAsync(Message(1, "/account"), default);
        Assert.Contains(expected, repo.Replies[1].Text);
        Assert.Equal(original, repo.Registrations[42]);
        Assert.Empty(repo.ProfileDialogs);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("4В", null)]
    [InlineData(null, "2")]
    public async Task MissingFieldsAreRequestedAndSavedWithoutOverwritingOtherProfileData(string? building, string? apartment)
    {
        var original = Profile with { BuildingNumber = building, ApartmentNumber = apartment };
        var (repo, processor) = Setup(original);
        await processor.ProcessAsync(Message(1, "/account"), default);
        long id = 2;
        if (building is null)
        {
            Assert.Equal(ReplyKeyboard.RegistrationBuilding, repo.Replies[1].Keyboard);
            await processor.ProcessAsync(Message(id++, "Будинок 4Д"), default);
        }
        if (apartment is null)
        {
            await processor.ProcessAsync(Message(id++, "1000"), default);
            Assert.Equal(original, repo.Registrations[42]);
            await processor.ProcessAsync(Message(id++, "2"), default);
        }
        Assert.Contains(building == "4В" ? "0002194в002" : "0002194д002", repo.Replies[id - 1].Text);
        var updated = repo.Registrations[42];
        Assert.Equal(original.FirstName, updated.FirstName);
        Assert.Equal(original.Phone, updated.Phone);
        Assert.Equal(original.ResidentType, updated.ResidentType);
        Assert.Equal(original.ConsentAt, updated.ConsentAt);
        Assert.Equal(original.ConsentVersion, updated.ConsentVersion);
        Assert.Equal(original.Version + 1, updated.Version);
        await processor.ProcessAsync(Message(id - 1, apartment is null ? "2" : "Будинок 4Д"), default);
        Assert.Equal(updated, repo.Registrations[42]);
    }

    [Fact]
    public async Task UnregisteredUserCannotReadOrChangeAccount()
    {
        var (repo, processor) = Setup(Profile);
        repo.Residents.Remove(42);
        await processor.ProcessAsync(Message(1, "/account"), default);
        Assert.DoesNotContain("000219", repo.Replies[1].Text);
        Assert.Equal(Profile, repo.Registrations[42]);
        Assert.Empty(repo.ProfileDialogs);
    }

    [Fact]
    public async Task CancellationAndChangedProfileDoNotOverwriteData()
    {
        var original = Profile with { ApartmentNumber = null };
        var (repo, processor) = Setup(original);
        await processor.ProcessAsync(Message(1, "/account"), default);
        await processor.ProcessAsync(Message(2, "/cancel"), default);
        Assert.Equal(original, repo.Registrations[42]);
        await processor.ProcessAsync(Message(3, "/account"), default);
        repo.Registrations[42] = original with { Version = 7, Email = "new@example.com" };
        await processor.ProcessAsync(Message(4, "1"), default);
        Assert.Null(repo.Registrations[42].ApartmentNumber);
        Assert.Contains("Профіль змінився", repo.Replies[4].Text);
    }
}
