using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RequestLookupTests
{
    private static CommandHandler Handler(MemoryRepository repository) => new(repository, "OsbbBot", "building");
    private static CommandEnvelope Message(long id, string text, long user = 42) => new(id, user, user, text);
    private static async Task<CommandOutcome> Apply(MemoryRepository repository, long id, string text)
    {
        var command = Message(id, text);
        var outcome = await Handler(repository).HandleAsync(command, default);
        await repository.CommitAsync(command, outcome, default);
        return outcome;
    }
    private static MaintenanceRequest Request(long owner = 42, string complex = "building") =>
        new(7, owner, complex, "інше", "двір", "Приватний опис", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("/request")]
    [InlineData("/REQUEST@OsbbBot")]
    [InlineData("/request abc")]
    public async Task PromptsAndResumesWithNextMessageAcrossHandlerInstances(string command)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        var prompt = await Apply(repository, 1, command);
        Assert.Contains("Введіть номер заявки", prompt.Reply);
        Assert.True(repository.Lookups[42].AwaitingNumber);
        var result = await Apply(repository, 2, " 7 ");
        Assert.Contains("Заявка №7", result.Reply);
        Assert.Contains("Приватний опис", result.Reply);
        Assert.False(repository.Lookups[42].AwaitingNumber);
        Assert.Null(result.NewRequest);
        Assert.Equal(2, repository.Lookups[42].Version);
        Assert.DoesNotContain("Заявка №7", (await Apply(repository, 3, "7")).Reply);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-7")]
    [InlineData("7 8")]
    [InlineData("9223372036854775808")]
    public async Task InvalidNumberKeepsWaiting(string text)
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/request");
        var invalid = await Apply(repository, 2, text);
        Assert.Contains("Введіть номер заявки", invalid.Reply);
        Assert.True(repository.Lookups[42].AwaitingNumber);
        Assert.Equal(1, repository.Lookups[42].Version);
    }

    [Theory]
    [InlineData("/cancel")]
    [InlineData("/help")]
    [InlineData("/my")]
    [InlineData("/new")]
    public async Task AnotherCommandClearsWaitingState(string command)
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/request");
        await Apply(repository, 2, command);
        Assert.False(repository.Lookups[42].AwaitingNumber);
        Assert.DoesNotContain("Заявка №7", (await Apply(repository, 3, "7")).Reply);
    }

    [Fact]
    public async Task InlineNumberStillWorksAndClearsWaitingState()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/request");
        Assert.Contains("Заявка №7", (await Apply(repository, 2, "/request 7")).Reply);
        Assert.False(repository.Lookups[42].AwaitingNumber);
    }

    [Theory]
    [InlineData(99, "building")]
    [InlineData(42, "other-complex")]
    public async Task NumberReplyCannotReadAnotherOwnerOrComplex(long owner, string complex)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request(owner, complex));
        await Apply(repository, 1, "/request");
        var outcome = await Apply(repository, 2, "7");
        Assert.Contains("не знайдено", outcome.Reply);
        Assert.DoesNotContain("Приватний опис", outcome.Reply);
    }

    [Fact]
    public async Task RevokedMembershipBlocksNextMessageAndUnregisteredUsersCannotStartLookup()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/request");
        repository.Residents.Remove(42);
        var outcome = await Apply(repository, 2, "7");
        Assert.DoesNotContain("Приватний опис", outcome.Reply);
        Assert.Null(outcome.Lookup);
        var denied = await Handler(repository).HandleAsync(Message(3, "/request", 99), default);
        Assert.Null(denied.Lookup);
        Assert.Contains("/start", denied.Reply);
    }

    [Fact]
    public async Task LookupDoesNotConsumeOrDiscardCreationDraft()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/new");
        var draft = repository.Drafts[42];
        await Apply(repository, 2, "/request");
        await Apply(repository, 3, "7");
        Assert.Equal(draft, repository.Drafts[42]);
        Assert.Contains("Де виникла проблема", (await Apply(repository, 4, "/new")).Reply);
    }

    [Fact]
    public async Task ReplayedPromptAndDeliveryRetryDoNotReopenCompletedLookup()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        await processor.ProcessAsync(Message(1, "/request"), default);
        telegram.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Message(2, "7"), default));
        Assert.False(repository.Lookups[42].AwaitingNumber);
        telegram.Fail = false;
        await processor.ProcessAsync(Message(2, "7"), default);
        await processor.ProcessAsync(Message(1, "/request"), default);
        Assert.False(repository.Lookups[42].AwaitingNumber);
        Assert.Equal(2, repository.CommitCount);
        Assert.Equal(2, telegram.Messages.Count);
    }
}
