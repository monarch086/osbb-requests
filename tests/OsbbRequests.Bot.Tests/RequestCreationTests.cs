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
        Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
        Assert.Empty(repository.Requests);
        var resume = await Handler(repository).HandleAsync(Message(5, "/new"), default);
        Assert.Contains("Опишіть", resume.Reply);
        Assert.Null(resume.Draft);
        await Apply(repository, 6, "Не працює освітлення");
        var request = Assert.Single(repository.Requests);
        Assert.Equal(6, request.Id);
        Assert.Equal(RequestScope.Building, request.Scope);
        Assert.Equal("4Д", request.BuildingNumber);
        Assert.Equal(25, request.Floor);
        Assert.Equal("building", request.BuildingId);
        Assert.Equal(RequestVisibility.Public, request.Visibility);
        Assert.Empty(request.Location);
        Assert.Equal(RequestDraftStage.Completed, repository.Drafts[42].Stage);
        Assert.Null(repository.Drafts[42].Location);
        Assert.Contains("Будинок 4Д", repository.Replies[6].Text);
        Assert.Contains("поверх 25", repository.Replies[6].Text);
    }

    [Fact]
    public async Task ComplexWizardSkipsFloorAndCancellationClearsDraft()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        await Apply(repository, 2, "Весь комплекс");
        Assert.Equal(RequestDraftStage.Category, repository.Drafts[42].Stage);
        await Apply(repository, 3, "прибирання");
        await Apply(repository, 5, "/cancel");
        Assert.Equal(RequestDraftStage.Cancelled, repository.Drafts[42].Stage);
        Assert.Null(repository.Drafts[42].Location);
        Assert.Empty(repository.Requests);
        await Apply(repository, 6, "/new");
        Assert.Equal(RequestDraftStage.Scope, repository.Drafts[42].Stage);
    }

    [Theory]
    [InlineData("/new комплекс | - | електрика | Не працює освітлення", RequestScope.Complex, null, null)]
    [InlineData("/new 4Б | 1 | сантехніка | Протікає труба", RequestScope.Building, "4Б", 1)]
    [InlineData("/new 4Д | 25 | сантехніка | Протікає труба", RequestScope.Building, "4Д", 25)]
    [InlineData("/new 4В | 12 | сантехніка | Протікає труба", RequestScope.Building, "4В", 12)]
    [InlineData("/new 4Г | - | інше | Зламано двері входу", RequestScope.Building, "4Г", null)]
    [InlineData("/new будинок 4б | 1 | інше | Зламано двері входу", RequestScope.Building, "4Б", 1)]
    public async Task QuickCommandRequiresExplicitValidTarget(string text, RequestScope scope, string? building, int? floor)
    {
        var outcome = await Handler(new()).HandleAsync(Message(1, text), default);
        Assert.NotNull(outcome.NewRequest);
        Assert.Equal(scope, outcome.NewRequest.Scope);
        Assert.Equal(building, outcome.NewRequest.BuildingNumber);
        Assert.Equal(floor, outcome.NewRequest.Floor);
        Assert.Empty(outcome.NewRequest.Location);
        Assert.Equal(RequestVisibility.Public, outcome.NewRequest.Visibility);
        Assert.Contains("для всіх мешканців комплексу", outcome.Reply);
    }

    [Theory]
    [InlineData("/new сантехніка | двір | Протікає труба")]
    [InlineData("/new 1 | 1 | інше | Зламано двері входу")]
    [InlineData("/new 4А | 1 | інше | Зламано двері входу")]
    [InlineData("/new 4Е | 1 | інше | Зламано двері входу")]
    [InlineData("/new 4B | 1 | інше | Зламано двері входу")]
    [InlineData("/new 0 | 1 | інше | Зламано двері входу")]
    [InlineData("/new 5 | 1 | інше | Зламано двері входу")]
    [InlineData("/new 4Б | 0 | інше | Зламано двері входу")]
    [InlineData("/new 4Б | 26 | інше | Зламано двері входу")]
    [InlineData("/new комплекс | 2 | інше | Зламано двері входу")]
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
        foreach (var (id, text) in new[] { (1, "/new"), (2, "Весь комплекс"), (3, "інше") })
        {
            await processor.ProcessAsync(Message(id, text), default);
            await processor.ProcessAsync(Message(id, text), default);
        }
        Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
        telegram.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Message(4, "Пошкоджено покриття доріжки"), default));
        Assert.Single(repository.Requests);
        telegram.Fail = false;
        await processor.ProcessAsync(Message(4, "Пошкоджено покриття доріжки"), default);
        Assert.Single(repository.Requests);
        Assert.Equal(4, repository.CommitCount);
        Assert.Equal(4, telegram.Messages.Count);
    }

    [Fact]
    public async Task ComplexWizardGoesFromCategoryDirectlyToDescriptionAndCreatesPublicRequest()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        await Apply(repository, 2, "Весь комплекс");
        await Apply(repository, 3, "прибирання");
        Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
        Assert.Contains("Опишіть проблему", repository.Replies[3].Text);
        Assert.Empty(repository.Requests);
        await Apply(repository, 4, "Прибрати сміття на території");
        var request = Assert.Single(repository.Requests);
        Assert.Equal(RequestScope.Complex, request.Scope);
        Assert.Null(request.BuildingNumber);
        Assert.Null(request.Floor);
        Assert.Empty(request.Location);
        Assert.Equal(RequestVisibility.Public, request.Visibility);
        Assert.Contains("№4", (await Handler(repository).HandleAsync(Message(5, "/complex"), default)).Reply);
        var detail = await Handler(repository).HandleAsync(Message(6, "/request 4"), default);
        Assert.DoesNotContain("Місце:", detail.Reply);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyLocationStepAcceptsDescriptionWithoutCollectingLocation(bool resume)
    {
        var repository = new MemoryRepository();
        repository.Drafts[42] = new(42, "building", RequestDraftStage.Location, 5,
            RequestScope.Building, "4В", 12, "електрика");
        if (resume)
        {
            await Apply(repository, 1, "/new");
            Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
            Assert.Equal(6, repository.Drafts[42].Version);
            Assert.Contains("Опишіть проблему", repository.Replies[1].Text);
        }
        await Apply(repository, 2, "Не працює освітлення");
        var request = Assert.Single(repository.Requests);
        Assert.Empty(request.Location);
        Assert.Equal("Не працює освітлення", request.Description);
        Assert.Equal(RequestVisibility.Public, request.Visibility);
    }

    [Fact]
    public async Task PreviouslyEnteredLocationIsPreservedWhenCompletingOldDraft()
    {
        var repository = new MemoryRepository();
        repository.Drafts[42] = new(42, "building", RequestDraftStage.Description, 5,
            RequestScope.Building, "4В", 12, "електрика", "біля ліфта");
        await Apply(repository, 1, "Не працює освітлення");
        var request = Assert.Single(repository.Requests);
        Assert.Equal("біля ліфта", request.Location);
        Assert.Equal(RequestVisibility.Public, request.Visibility);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(2001)]
    public async Task DescriptionLengthIsValidatedInWizardAndQuickCommand(int length)
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        await Apply(repository, 2, "Весь комплекс");
        await Apply(repository, 3, "інше");
        await Apply(repository, 4, new string('О', length));
        Assert.Empty(repository.Requests);
        Assert.Equal(RequestDraftStage.Description, repository.Drafts[42].Stage);
        var quick = await Handler(repository).HandleAsync(Message(5, "/new 4Б | 1 | інше | " + new string('О', length)), default);
        Assert.Null(quick.NewRequest);
    }

    [Fact]
    public async Task LegacyRequestRemainsReadableWithoutInventingItsBuilding()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(new(7, 42, "building", "інше", "двір", "Стара заявка без прив’язки", DateTimeOffset.UtcNow));
        var detail = await Handler(repository).HandleAsync(Message(8, "/request 7"), default);
        Assert.Contains("Будинок не уточнено", detail.Reply);
        Assert.Contains("Стара заявка", detail.Reply);
        Assert.Equal(RequestVisibility.Private, repository.Requests[0].Visibility);
    }
}
