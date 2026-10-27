using System.Text.Json;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RequestEntranceTests
{
    private static CommandHandler Handler(MemoryRepository repository) => new(repository, "OsbbBot", "building");
    private static CommandEnvelope Message(long id, string text) => new(id, 42, 42, text);
    private static async Task<CommandOutcome> Apply(MemoryRepository repository, long id, string text)
    {
        var command = Message(id, text);
        var outcome = await Handler(repository).HandleAsync(command, default);
        await repository.CommitAsync(command, outcome, default);
        return outcome;
    }

    [Theory]
    [InlineData("4В", "1", 1)]
    [InlineData("4Г", "2", 2)]
    [InlineData("4Д", "Не зазначено", null)]
    public async Task BuildingSelectionAsksEntranceBeforeFloorAndPersistsChoice(string building, string choice, int? expected)
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        var prompt = await Apply(repository, 2, "Будинок " + building);
        Assert.Equal(ReplyKeyboard.RequestEntrance, prompt.Keyboard);
        Assert.Equal(RequestDraftStage.Entrance, repository.Drafts[42].Stage);
        var resume = await Apply(repository, 3, "/new");
        Assert.Equal(ReplyKeyboard.RequestEntrance, resume.Keyboard);
        await Apply(repository, 4, choice);
        Assert.Equal(RequestDraftStage.Floor, repository.Drafts[42].Stage);
        Assert.Equal(expected, repository.Drafts[42].Entrance);
        await Apply(repository, 5, "Парадне");
        await Apply(repository, 6, "інше");
        await Apply(repository, 7, "Зламано двері парадного");
        var request = Assert.Single(repository.Requests);
        Assert.Equal(expected, request.Entrance);
        Assert.Equal(0, request.Floor);
        Assert.Equal(expected, JsonSerializer.Deserialize<MaintenanceRequest>(JsonSerializer.Serialize(request))!.Entrance);
        foreach (var command in new[] { "/my", "/complex", "/request 7" })
        {
            var result = await Handler(repository).HandleAsync(Message(8, command), default);
            if (expected is null && command != "/request 7") Assert.DoesNotContain("під’їзд", result.Reply);
            else Assert.Contains("під’їзд " + (expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Не зазначено"), result.Reply);
        }
    }

    [Fact]
    public async Task Building4BAutomaticallyAssignsEntranceOneAndSkipsQuestion()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        var result = await Apply(repository, 2, "Будинок 4Б");
        Assert.Equal(RequestDraftStage.Floor, repository.Drafts[42].Stage);
        Assert.Equal(ReplyKeyboard.RequestFloor, result.Keyboard);
        Assert.Equal(1, repository.Drafts[42].Entrance);
        await Apply(repository, 3, "12");
        await Apply(repository, 4, "інше");
        await Apply(repository, 5, "Не працює освітлення");
        Assert.Equal(1, Assert.Single(repository.Requests).Entrance);
    }

    [Fact]
    public async Task InvalidEntranceCannotAdvanceAndCancellationClearsSelection()
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/new");
        await Apply(repository, 2, "Будинок 4В");
        await Apply(repository, 3, "3");
        Assert.Equal(RequestDraftStage.Entrance, repository.Drafts[42].Stage);
        await Apply(repository, 4, "2");
        await Apply(repository, 5, "/cancel");
        Assert.Null(repository.Drafts[42].Entrance);
        Assert.Equal(RequestDraftStage.Cancelled, repository.Drafts[42].Stage);
        Assert.Empty(repository.Requests);
    }

    [Fact]
    public async Task EntranceStepAndFinalizationRemainIdempotent()
    {
        var repository = new MemoryRepository();
        var processor = new CommandProcessor(repository, Handler(repository), new FakeTelegram());
        foreach (var (id, text) in new[] { (1, "/new"), (2, "Будинок 4В"), (3, "2"), (4, "12"), (5, "інше"), (6, "Не працює освітлення") })
        {
            await processor.ProcessAsync(Message(id, text), default);
            await processor.ProcessAsync(Message(id, text), default);
        }
        Assert.Equal(2, Assert.Single(repository.Requests).Entrance);
        Assert.Equal(6, repository.CommitCount);
    }

    [Theory]
    [InlineData("/new 4В | 2 | 12 | інше | Не працює освітлення", 2)]
    [InlineData("/new 4Г | Не зазначено | Парадне | інше | Не працює освітлення", null)]
    [InlineData("/new 4Б | 1 | - | інше | Не працює освітлення", 1)]
    [InlineData("/new 4Б | 12 | інше | Не працює освітлення", 1)]
    [InlineData("/new 4В | 12 | інше | Не працює освітлення | біля ліфта", null)]
    [InlineData("/new 4Д | 1 | 25 | інше | Не працює освітлення | біля ліфта", 1)]
    public async Task QuickCommandsSupportEntranceAndRetainLegacyDescriptionPipes(string command, int? expected)
    {
        var result = await Handler(new()).HandleAsync(Message(1, command), default);
        Assert.NotNull(result.NewRequest);
        Assert.Equal(expected, result.NewRequest.Entrance);
        if (command.EndsWith("біля ліфта", StringComparison.Ordinal)) Assert.EndsWith("| біля ліфта", result.NewRequest.Description);
    }

    [Theory]
    [InlineData("/new 4В | 3 | 12 | інше | Не працює освітлення")]
    [InlineData("/new 4Б | 2 | 12 | інше | Не працює освітлення")]
    [InlineData("/new комплекс | 1 | - | інше | Не працює освітлення")]
    public async Task InvalidQuickEntranceDoesNotCreateRequest(string command)
    {
        Assert.Null((await Handler(new()).HandleAsync(Message(1, command), default)).NewRequest);
    }

    [Theory]
    [InlineData("4Б")]
    [InlineData("4В")]
    public void ExistingPayloadsReadMissingEntranceAsUnspecifiedWithoutAutomaticAssignment(string building)
    {
        var json = $$"""
            {"Id":7,"OwnerId":42,"BuildingId":"complex","Category":"інше","Location":"","Description":"Стара заявка","CreatedAt":"2026-10-24T10:00:00Z","Scope":"Building","BuildingNumber":"{{building}}","Floor":12}
            """;
        var request = JsonSerializer.Deserialize<MaintenanceRequest>(json)!;
        Assert.Null(request.Entrance);
        Assert.Contains("під’їзд Не зазначено", ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor, request.Entrance));
        Assert.True(ComplexLayout.IsValid(request.Scope, request.BuildingNumber, request.Floor, request.Entrance));
    }

    [Fact]
    public void StoredDraftAndKeyboardNumericValuesRemainCompatible()
    {
        Assert.Equal(1, (int)RequestDraftStage.Floor);
        Assert.Equal(3, (int)RequestDraftStage.Location);
        Assert.Equal(5, (int)RequestDraftStage.Completed);
        Assert.Equal(3, (int)ReplyKeyboard.RequestFloor);
        Assert.Equal(4, (int)ReplyKeyboard.RequestCategory);
    }
}
