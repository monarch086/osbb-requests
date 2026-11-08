using OsbbRequests.Shared.Domain;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class KeyOrderTests
{
    private static CommandEnvelope Message(long id, string text) => new(id, 42, 42, text);
    private static ResidentRegistration Profile(ResidentType type) => new("resident", 42, "building", RegistrationStage.Approved, 4,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ResidentType: type);
    private static async Task Send(MemoryRepository repo, long id, string text)
    {
        var command = Message(id, text);
        var outcome = await new CommandHandler(repo, "Bot", "building").HandleAsync(command, default);
        await repo.CommitAsync(command, outcome, default);
    }

    [Theory]
    [InlineData(ResidentType.FamilyMember)]
    [InlineData(ResidentType.Tenant)]
    public async Task NonOwnersCannotSeeOrSelectKeyOrder(ResidentType type)
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(type);
        await Send(repo, 1, "/new");
        await Send(repo, 2, "4Б");
        await Send(repo, 3, "1");
        Assert.Equal(ReplyKeyboard.RequestCategory, repo.Replies[3].Keyboard);
        Assert.DoesNotContain(RequestCategories.KeyOrder, repo.Replies[3].Text);
        await Send(repo, 4, RequestCategories.KeyOrder);
        Assert.Equal(RequestDraftStage.Category, repo.Drafts[42].Stage);
        Assert.Empty(repo.Requests);
    }

    [Theory]
    [InlineData("Магніт", 2)]
    [InlineData("Перепустка", 5)]
    public async Task OwnerCreatesPrivateStructuredOrder(string type, int quantity)
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        await Send(repo, 1, "/new");
        await Send(repo, 2, "4Б");
        await Send(repo, 3, "1");
        Assert.Equal(ReplyKeyboard.OwnerRequestCategory, repo.Replies[3].Keyboard);
        await Send(repo, 4, RequestCategories.KeyOrder);
        Assert.Equal(ReplyKeyboard.KeyType, repo.Replies[4].Keyboard);
        await Send(repo, 5, type);
        await Send(repo, 6, "0");
        Assert.Empty(repo.Requests);
        var command = Message(7, quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var outcome = await new CommandHandler(repo, "Bot", "building").HandleAsync(command, default);
        Assert.Equal(4, outcome.OwnerProfileVersion);
        var request = Assert.IsType<MaintenanceRequest>(outcome.NewRequest);
        Assert.Equal(RequestVisibility.Private, request.Visibility);
        Assert.Equal(type, request.KeyType);
        Assert.Equal(quantity, request.KeyQuantity);
        await repo.CommitAsync(command, outcome, default);
        Assert.Single(repo.Requests);
    }

    [Fact]
    public async Task OwnerRevokedDuringDraftCannotFinishOrder()
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        await Send(repo, 1, "/new");
        await Send(repo, 2, "4Б");
        await Send(repo, 3, "1");
        await Send(repo, 4, RequestCategories.KeyOrder);
        await Send(repo, 5, "Магніт");
        repo.Registrations[42] = Profile(ResidentType.Tenant) with { Version = 5 };
        await Send(repo, 6, "2");
        Assert.Empty(repo.Requests);
        Assert.Contains("лише підтвердженим власникам", repo.Replies[6].Text);
    }
}
