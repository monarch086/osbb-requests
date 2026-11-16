using OsbbRequests.Shared.Domain;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class KeyOrderTests
{
    private static CommandEnvelope Message(long id, string text) => new(id, 42, 42, text);
    private static ResidentRegistration Profile(ResidentType type, long user = 42, string phone = "+380671234567") => new("resident", user, "building", RegistrationStage.Approved, 4,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: phone, ResidentType: type);
    private static async Task Send(MemoryRepository repo, long id, string text)
    {
        var command = Message(id, text);
        var outcome = await new CommandHandler(repo, "Bot", "building").HandleAsync(command, default);
        await repo.CommitAsync(command, outcome, default);
    }

    [Theory]
    [InlineData(ResidentType.FamilyMember)]
    [InlineData(ResidentType.Tenant)]
    [InlineData(ResidentType.Employee)]
    public async Task NonOwnersCannotSeeOrSelectKeyOrder(ResidentType type)
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(type);
        await Send(repo, 1, "/new");
        Assert.Equal(ReplyKeyboard.RequestCategory, repo.Replies[1].Keyboard);
        Assert.DoesNotContain(RequestCategories.KeyOrder, repo.Replies[1].Text);
        await Send(repo, 2, RequestCategories.KeyOrder);
        Assert.Equal(RequestDraftStage.InitialCategory, repo.Drafts[42].Stage);
        Assert.Empty(repo.Requests);
    }

    [Theory]
    [InlineData(ResidentType.FamilyMember)]
    [InlineData(ResidentType.Owner)]
    [InlineData(ResidentType.Tenant)]
    [InlineData(ResidentType.Employee)]
    public async Task OwnerCreatesPrivateOrderWithSeparatePhonesWithoutLocation(ResidentType recipientType)
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        repo.Registrations[99] = Profile(recipientType, 99, "+380931234567");
        await Send(repo, 1, "/new");
        Assert.Equal(ReplyKeyboard.OwnerRequestCategory, repo.Replies[1].Keyboard);
        await Send(repo, 2, RequestCategories.KeyOrder);
        Assert.Equal(ReplyKeyboard.KeyType, repo.Replies[2].Keyboard);
        await Send(repo, 3, "Магніт");
        await Send(repo, 4, "0000000000");
        Assert.Equal(RequestDraftStage.KeyPhone, repo.Drafts[42].Stage);
        Assert.Empty(repo.Requests);
        await Send(repo, 5, "0671234567");
        Assert.Equal(ReplyKeyboard.KeyNext, repo.Replies[5].Keyboard);
        await Send(repo, 6, "Додати ще один ключ");
        await Send(repo, 7, "Перепустка");
        await Send(repo, 8, "+380931234567");
        var command = Message(9, "Завершити створення заявки");
        var outcome = await new CommandHandler(repo, "Bot", "building").HandleAsync(command, default);
        Assert.Equal(4, outcome.OwnerProfileVersion);
        var request = Assert.IsType<MaintenanceRequest>(outcome.NewRequest);
        Assert.Equal(RequestVisibility.Private, request.Visibility);
        Assert.Null(request.Scope);
        Assert.Equal(2, request.KeyQuantity);
        Assert.Equal(new KeyOrderItem("Магніт", "+380671234567"), request.KeyItems![0]);
        Assert.Equal(new KeyOrderItem("Перепустка", "+380931234567"), request.KeyItems[1]);
        await repo.CommitAsync(command, outcome, default);
        Assert.Single(repo.Requests);
    }

    [Fact]
    public async Task UnknownPhoneCanBeRetriedOrCancelled()
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        await Send(repo, 1, "/new");
        await Send(repo, 2, RequestCategories.KeyOrder);
        await Send(repo, 3, "Магніт");
        await Send(repo, 4, "0999999999");
        Assert.Contains("не знайдено", repo.Replies[4].Text);
        Assert.Equal(RequestDraftStage.KeyPhone, repo.Drafts[42].Stage);
        await Send(repo, 5, "Скасувати");
        Assert.Equal(RequestDraftStage.Cancelled, repo.Drafts[42].Stage);
        Assert.Empty(repo.Requests);
    }

    [Fact]
    public async Task CancellationAfterAddingKeyLeavesNoRequest()
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        await Send(repo, 1, "/new");
        await Send(repo, 2, RequestCategories.KeyOrder);
        await Send(repo, 3, "Магніт");
        await Send(repo, 4, "0671234567");
        await Send(repo, 5, "Скасувати");
        Assert.Empty(repo.Requests);
        Assert.Equal(RequestDraftStage.Cancelled, repo.Drafts[42].Stage);
        Assert.Null(repo.Drafts[42].KeyItems);
    }

    [Fact]
    public async Task RepeatedFinalCommandCreatesOnlyOneOrder()
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        await Send(repo, 1, "/new");
        await Send(repo, 2, RequestCategories.KeyOrder);
        await Send(repo, 3, "Магніт");
        await Send(repo, 4, "0671234567");
        var processor = new CommandProcessor(repo, new CommandHandler(repo, "Bot", "building"), new FakeTelegram());
        await processor.ProcessAsync(Message(5, "Завершити створення заявки"), default);
        await processor.ProcessAsync(Message(5, "Завершити створення заявки"), default);
        Assert.Single(repo.Requests);
    }

    [Fact]
    public async Task OwnerRevokedDuringDraftCannotFinishOrder()
    {
        var repo = new MemoryRepository();
        repo.Registrations[42] = Profile(ResidentType.Owner);
        await Send(repo, 1, "/new");
        await Send(repo, 2, RequestCategories.KeyOrder);
        await Send(repo, 3, "Магніт");
        await Send(repo, 4, "+380671234567");
        repo.Registrations[42] = Profile(ResidentType.Tenant) with { Version = 5 };
        await Send(repo, 5, "Завершити створення заявки");
        Assert.Empty(repo.Requests);
        Assert.Contains("лише підтвердженим власникам", repo.Replies[5].Text);
    }
}
