using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Domain;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RequestCreationTests
{
    private static CommandHandler Handler(MemoryRepository repository) => new(repository, "OsbbBot", "building");
    private static CommandEnvelope Message(long id, string text, long user = 42) => new(id, user, user, text);
    private static async Task Apply(MemoryRepository repository, long id, string text)
    {
        var command = Message(id, text);
        await repository.CommitAsync(command, await Handler(repository).HandleAsync(command, default), default);
    }

    [Fact]
    public async Task BuildingWizardPersistsAndResumesThenCreatesScopedRequestOnlyAfterDescription()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        Assert.Equal(RequestDraftStage.Scope, repository.Drafts[42].Stage);
        await Apply(repository, 2, "Будинок 4Д");
        Assert.Equal(RequestDraftStage.Floor, repository.Drafts[42].Stage);
        await Apply(repository, 3, "25");
        await Apply(repository, 4, "електрика");
        await Apply(repository, 5, "біля ліфта");
        Assert.Empty(repository.Requests);
        var resume = await Handler(repository).HandleAsync(Message(6, "/new"), default);
        Assert.Contains("Опишіть", resume.Reply);
        Assert.Null(resume.Draft);
        await Apply(repository, 7, "Не працює освітлення");
        var request = Assert.Single(repository.Requests);
        Assert.Equal(7, request.Id);
        Assert.Equal(RequestScope.Building, request.Scope);
        Assert.Equal("4Д", request.BuildingNumber);
        Assert.Equal(25, request.Floor);
        Assert.Equal("building", request.BuildingId);
        Assert.Equal(RequestDraftStage.Completed, repository.Drafts[42].Stage);
        Assert.Null(repository.Drafts[42].Location);
        Assert.Contains("Будинок 4Д", repository.Replies[7].Text);
        Assert.Contains("поверх 25", repository.Replies[7].Text);
    }

    [Fact]
    public async Task ComplexWizardSkipsFloorAndCancellationClearsDraft()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        await Apply(repository, 2, "Весь комплекс");
        Assert.Equal(RequestDraftStage.Category, repository.Drafts[42].Stage);
        await Apply(repository, 3, "прибирання");
        await Apply(repository, 4, "двір біля майданчика");
        await Apply(repository, 5, "/cancel");
        Assert.Equal(RequestDraftStage.Cancelled, repository.Drafts[42].Stage);
        Assert.Null(repository.Drafts[42].Location);
        Assert.Empty(repository.Requests);
        await Apply(repository, 6, "/new");
        Assert.Equal(RequestDraftStage.Scope, repository.Drafts[42].Stage);
    }

    [Theory]
    [InlineData("/new комплекс | - | електрика | двір | Не працює освітлення", RequestScope.Complex, null, null)]
    [InlineData("/new 4Б | 1 | сантехніка | біля ліфта | Протікає труба", RequestScope.Building, "4Б", 1)]
    [InlineData("/new 4Д | 25 | сантехніка | біля ліфта | Протікає труба", RequestScope.Building, "4Д", 25)]
    [InlineData("/new 4В | 12 | сантехніка | біля ліфта | Протікає труба", RequestScope.Building, "4В", 12)]
    [InlineData("/new 4Г | - | інше | вхід | Зламано двері входу", RequestScope.Building, "4Г", null)]
    [InlineData("/new будинок 4б | 1 | інше | вхід | Зламано двері входу", RequestScope.Building, "4Б", 1)]
    public async Task QuickCommandRequiresExplicitValidTarget(string text, RequestScope scope, string? building, int? floor)
    {
        var outcome = await Handler(new()).HandleAsync(Message(1, text), default);
        Assert.NotNull(outcome.NewRequest);
        Assert.Equal(scope, outcome.NewRequest.Scope);
        Assert.Equal(building, outcome.NewRequest.BuildingNumber);
        Assert.Equal(floor, outcome.NewRequest.Floor);
    }

    [Theory]
    [InlineData("/new сантехніка | двір | Протікає труба")]
    [InlineData("/new 1 | 1 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 4А | 1 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 4Е | 1 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 4B | 1 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 0 | 1 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 5 | 1 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 4Б | 0 | інше | вхід | Зламано двері входу")]
    [InlineData("/new 4Б | 26 | інше | вхід | Зламано двері входу")]
    [InlineData("/new комплекс | 2 | інше | вхід | Зламано двері входу")]
    public async Task AmbiguousOrOutOfRangeQuickCommandDoesNotCreateRequest(string text)
    {
        var outcome = await Handler(new()).HandleAsync(Message(1, text), default);
        Assert.Null(outcome.NewRequest);
        Assert.Null(outcome.Draft);
    }

    [Fact]
    public async Task InvalidWizardInputCannotAdvanceAndRevokedAccessCannotCompleteDraft()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        await Apply(repository, 2, "Будинок 4А");
        Assert.Equal(RequestDraftStage.Scope, repository.Drafts[42].Stage);
        await Apply(repository, 3, "Будинок 4Б");
        await Apply(repository, 4, "26");
        Assert.Equal(RequestDraftStage.Floor, repository.Drafts[42].Stage);
        await Apply(repository, 5, "Без поверху");
        await Apply(repository, 6, "інше");
        await Apply(repository, 7, "фасад");
        repository.Residents.Clear();
        await Apply(repository, 8, "Пошкоджено облицювання");
        Assert.Empty(repository.Requests);
        Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
    }

    [Fact]
    public async Task RetriedWizardStepsAndFinalDescriptionAreIdempotentEvenAfterDeliveryFailure()
    {
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        foreach (var (id, text) in new[] { (1, "/new"), (2, "Весь комплекс"), (3, "інше"), (4, "двір") })
        {
            await processor.ProcessAsync(Message(id, text), default);
            await processor.ProcessAsync(Message(id, text), default);
        }
        Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
        telegram.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Message(5, "Пошкоджено покриття доріжки"), default));
        Assert.Single(repository.Requests);
        telegram.Fail = false;
        await processor.ProcessAsync(Message(5, "Пошкоджено покриття доріжки"), default);
        Assert.Single(repository.Requests);
        Assert.Equal(5, repository.CommitCount);
        Assert.Equal(5, telegram.Messages.Count);
    }

    [Fact]
    public async Task LegacyRequestRemainsReadableWithoutInventingItsBuilding()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(new(7, 42, "building", "інше", "двір", "Стара заявка без прив’язки", DateTimeOffset.UtcNow));
        var detail = await Handler(repository).HandleAsync(Message(8, "/request 7"), default);
        Assert.Contains("Будинок не уточнено", detail.Reply);
        Assert.Contains("Стара заявка", detail.Reply);
    }
}
