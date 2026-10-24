using OsbbRequests.Shared.Models;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Domain;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class CommandTests
{
    private static CommandHandler Handler(MemoryRepository repository) => new(repository, "OsbbBot", "building");
    private static CommandEnvelope Command(string text, long updateId = 100, long userId = 42) =>
        new(updateId, userId, userId, text);

    [Theory]
    [InlineData("/START", "start", "")]
    [InlineData("/HeLp@oSbBbOt", "help", "")]
    [InlineData("/new@OsbbBot сантехніка | кухня | Протікає труба", "new", "сантехніка | кухня | Протікає труба")]
    [InlineData("/request\n123", "request", "123")]
    public void ParsesCommands(string text, string name, string arguments)
    {
        var command = BotCommand.Parse(text, "OsbbBot");
        Assert.NotNull(command);
        Assert.Equal(name, command.Name);
        Assert.Equal(arguments, command.Arguments);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("/help@OtherBot")]
    [InlineData("/help@OsbbBot@OtherBot")]
    [InlineData("/")]
    public void DoesNotTreatPlainTextOrOtherBotAsACommand(string text) =>
        Assert.Null(BotCommand.Parse(text, "OsbbBot"));

    [Fact]
    public async Task CreatesPublicNewRequest()
    {
        var outcome = await Handler(new MemoryRepository()).HandleAsync(
            Command("/new 4В | 12 | сантехніка | Протікає труба"), CancellationToken.None);
        var request = Assert.IsType<MaintenanceRequest>(outcome.NewRequest);
        Assert.Equal(42, request.OwnerId);
        Assert.Equal("building", request.BuildingId);
        Assert.Equal("4В", request.BuildingNumber);
        Assert.Equal(12, request.Floor);
        Assert.Equal(RequestStatus.New, request.Status);
        Assert.Equal(RequestVisibility.Public, request.Visibility);
        Assert.Empty(request.Location);
        Assert.Equal("Протікає труба", request.Description);
        Assert.Contains("№100", outcome.Reply);
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/new невідомо | двір | Зламано освітлення")]
    [InlineData("/new електрика | | Зламано освітлення")]
    [InlineData("/new електрика | двір | коротко")]
    public async Task ValidatesNewRequest(string text)
    {
        var outcome = await Handler(new MemoryRepository()).HandleAsync(Command(text), CancellationToken.None);
        Assert.Null(outcome.NewRequest);
    }

    [Theory]
    [InlineData("/new електрика | двір | Зламано освітлення")]
    [InlineData("/my")]
    [InlineData("/request 5")]
    public async Task RequiresResidentAccess(string text)
    {
        var repository = new MemoryRepository();
        var outcome = await Handler(repository).HandleAsync(Command(text, userId: 99), CancellationToken.None);
        Assert.Null(outcome.NewRequest);
        Assert.Contains("/register", outcome.Reply);
        Assert.Equal(0, repository.Queries);
    }

    [Fact]
    public async Task CannotReadAnotherResidentsRequest()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(new MaintenanceRequest(7, 99, "building", "інше", "квартира", "Приватний опис",
            DateTimeOffset.UtcNow));
        var outcome = await Handler(repository).HandleAsync(Command("/request 7"), CancellationToken.None);
        Assert.DoesNotContain("Приватний опис", outcome.Reply);
        Assert.Contains("не знайдено", outcome.Reply);
    }

    [Fact]
    public async Task PaginatesOwnRequestsWithinTelegramLimit()
    {
        var repository = new MemoryRepository();
        for (var i = 1; i <= 11; i++)
            repository.Requests.Add(new MaintenanceRequest(i, 42, "building", "місця загального користування",
                new string('М', 200), new string('О', 2000), DateTimeOffset.UtcNow));
        repository.Requests.Add(new MaintenanceRequest(99, 99, "building", "інше", "Секретне місце", "Чужі дані",
            DateTimeOffset.UtcNow));
        var page = await Handler(repository).HandleAsync(Command("/my"), CancellationToken.None);
        var ids = new List<long>();
        while (true)
        {
            Assert.DoesNotContain("Секретне місце", page.Reply);
            Assert.DoesNotContain("Чужі дані", page.Reply);
            Assert.Contains(new string('О', 2000), page.Reply);
            Assert.True(page.Reply.Length <= 4096);
            ids.AddRange(System.Text.RegularExpressions.Regex.Matches(page.Reply, @"^№(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            var parts = page.Reply.Split("Наступна сторінка: ");
            if (parts.Length == 1) break;
            Assert.True(ids.Count <= 11);
            page = await Handler(repository).HandleAsync(Command(parts[1]), CancellationToken.None);
        }
        Assert.Equal(Enumerable.Range(1, 11).Reverse().Select(id => (long)id), ids);
    }

    [Fact]
    public async Task ReplayedUpdateDoesNotCreateOrSendAgain()
    {
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        var command = Command("/new 4В | 12 | сантехніка | Протікає труба");
        await processor.ProcessAsync(command, CancellationToken.None);
        await processor.ProcessAsync(command, CancellationToken.None);
        Assert.Single(repository.Requests);
        Assert.Single(telegram.Messages);
        Assert.Equal(1, repository.CommitCount);
    }

    [Fact]
    public async Task DeliveryFailureRetriesStoredReplyWithoutCreatingDuplicateRequest()
    {
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram { Fail = true };
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        var command = Command("/new 4В | 12 | сантехніка | Протікає труба");
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(command, CancellationToken.None));
        Assert.Single(repository.Requests);
        Assert.False(repository.Replies[100].Delivered);
        telegram.Fail = false;
        await processor.ProcessAsync(command, CancellationToken.None);
        Assert.Single(repository.Requests);
        Assert.Single(telegram.Messages);
        Assert.True(repository.Replies[100].Delivered);
    }

    [Fact]
    public async Task NeverSendsBeforePersisting()
    {
        var repository = new MemoryRepository { FailCommit = true };
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Command("/help"), CancellationToken.None));
        Assert.Empty(telegram.Messages);
    }

    [Fact]
    public async Task RejectsMismatchedStoredIdentity()
    {
        var repository = new MemoryRepository();
        repository.Replies[100] = new StoredReply(100, 99, 99, "Чужі дані", false);
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Command("/help"), CancellationToken.None));
        Assert.Empty(telegram.Messages);
    }
}
