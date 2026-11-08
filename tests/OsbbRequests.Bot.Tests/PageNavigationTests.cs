using System.Net;
using System.Text.Json;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class PageNavigationTests
{
    private static MaintenanceRequest Request(int id, long owner = 42, RequestVisibility visibility = RequestVisibility.Public) =>
        new(id, owner, "building", "Інше", "", "Достатній опис заявки", DateTimeOffset.UnixEpoch.AddMinutes(id),
            Visibility: visibility, Scope: RequestScope.Complex);
    private static (MemoryRepository Repo, FakeTelegram Telegram, CommandProcessor Processor) Setup()
    {
        var repo = new MemoryRepository();
        var telegram = new FakeTelegram();
        return (repo, telegram, new(repo, new CommandHandler(repo, "Bot", "building"), telegram, "building"));
    }
    private static CommandEnvelope Message(long id, string text, long user = 42) => new(id, user, user, text);

    [Theory]
    [InlineData("/my")]
    [InlineData("/complex")]
    public async Task NextMovesToNextPageAndReturnsMainMenuOnLastPage(string command)
    {
        var (repo, telegram, processor) = Setup();
        for (var i = 1; i <= 11; i++) repo.Requests.Add(Request(i));
        await processor.ProcessAsync(Message(1, command), default);
        Assert.Equal(ReplyKeyboard.PageNavigation, telegram.Keyboards[^1]);
        Assert.NotNull(repo.PageNavigations[42].NextCommand);
        await processor.ProcessAsync(Message(2, "Далі"), default);
        // /complex has five results per page; continue until the cursor ends.
        if (command == "/complex") await processor.ProcessAsync(Message(3, "Далі"), default);
        Assert.Contains("№1 ", telegram.Messages[^1].Text);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
        Assert.Null(repo.PageNavigations[42].NextCommand);
    }

    [Fact]
    public async Task PersonalListDoesNotShowNextForRequestsFromAnotherComplex()
    {
        var (repo, telegram, processor) = Setup();
        for (var id = 4; id <= 13; id++) repo.Requests.Add(Request(id));
        for (var id = 1; id <= 3; id++) repo.Requests.Add(Request(id) with { BuildingId = "other" });
        await processor.ProcessAsync(Message(1, "/my"), default);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
        Assert.DoesNotContain("Наступна сторінка", telegram.Messages[^1].Text);
    }

    [Fact]
    public async Task PageKeyboardContainsCancelAndNext()
    {
        using var capture = new KeyboardCapture();
        using var http = new HttpClient(capture);
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(42, "Список", default, ReplyKeyboard.PageNavigation);
        using var json = JsonDocument.Parse(capture.Body!);
        var row = json.RootElement.GetProperty("reply_markup").GetProperty("keyboard")[0];
        Assert.Equal(new[] { "Скасувати", "Далі" }, row.EnumerateArray().Select(value => value.GetProperty("text").GetString()).ToArray());
    }

    private sealed class KeyboardCapture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
        }
    }

    [Fact]
    public async Task CancelClosesNavigationAndDoesNotDeleteRequests()
    {
        var (repo, telegram, processor) = Setup();
        for (var i = 1; i <= 11; i++) repo.Requests.Add(Request(i));
        await processor.ProcessAsync(Message(1, "/my"), default);
        await processor.ProcessAsync(Message(2, "Скасувати"), default);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
        Assert.Null(repo.PageNavigations[42].NextCommand);
        Assert.Equal(11, repo.Requests.Count);
        await processor.ProcessAsync(Message(3, "Далі"), default);
        Assert.DoesNotContain("Ваші заявки:", telegram.Messages[^1].Text);
    }

    [Fact]
    public async Task ComplexCanContinueAfterAnEmptyFilteredPage()
    {
        var (repo, telegram, processor) = Setup();
        for (var i = 1; i <= 6; i++) repo.Requests.Add(Request(i, 99, i == 1 ? RequestVisibility.Public : RequestVisibility.Private));
        await processor.ProcessAsync(Message(1, "/complex"), default);
        Assert.Contains("На цій сторінці немає", telegram.Messages[^1].Text);
        Assert.Equal(ReplyKeyboard.PageNavigation, telegram.Keyboards[^1]);
        await processor.ProcessAsync(Message(2, "Далі"), default);
        Assert.Contains("№1", telegram.Messages[^1].Text);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
    }

    [Fact]
    public async Task CancellingPagesReturnsWorkingMenuWithoutDiscardingAnOlderDraft()
    {
        var (repo, telegram, processor) = Setup();
        for (var i = 1; i <= 11; i++) repo.Requests.Add(Request(i));
        repo.Drafts[42] = new(42, "building", RequestDraftStage.Description, 1, Category: "Інше", Scope: RequestScope.Complex);
        await processor.ProcessAsync(Message(1, "/my"), default);
        await processor.ProcessAsync(Message(2, "Скасувати"), default);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
        await processor.ProcessAsync(Message(3, "Контакти майстрів"), default);
        Assert.Contains("Телефони диспетчерів", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(4, "Створити заявку"), default);
        Assert.Contains("Опишіть проблему", telegram.Messages[^1].Text);
        Assert.Equal(RequestDraftStage.Description, repo.Drafts[42].Stage);
    }

    [Fact]
    public async Task ExpiredNavigationDoesNotOpenNextPage()
    {
        var (repo, telegram, processor) = Setup();
        repo.PageNavigations[42] = new(42, "building", "/my 12", 1, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
        await processor.ProcessAsync(Message(1, "Далі"), default);
        Assert.DoesNotContain("Ваші заявки:", telegram.Messages[^1].Text);
    }

    [Fact]
    public async Task RevokedResidentCannotUseNextButton()
    {
        var (repo, telegram, processor) = Setup();
        repo.PageNavigations[42] = new(42, "building", "/my 12", 1, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        repo.Residents.Remove(42);
        await processor.ProcessAsync(Message(1, "Далі"), default);
        Assert.DoesNotContain("Ваші заявки:", telegram.Messages[^1].Text);
        Assert.Equal(ReplyKeyboard.MainGuest, telegram.Keyboards[^1]);
    }

    [Fact]
    public async Task DuplicateNextUpdateDoesNotAdvanceTwiceAndAnotherUserCannotUseCursor()
    {
        var (repo, telegram, processor) = Setup();
        for (var i = 1; i <= 11; i++) repo.Requests.Add(Request(i));
        await processor.ProcessAsync(Message(1, "/complex"), default);
        var next = Message(2, "Далі");
        await processor.ProcessAsync(next, default);
        var saved = repo.PageNavigations[42];
        await processor.ProcessAsync(next, default);
        Assert.Equal(saved, repo.PageNavigations[42]);
        Assert.Equal(2, telegram.Messages.Count);
        repo.Residents.Add(99);
        await processor.ProcessAsync(Message(3, "Далі", 99), default);
        Assert.DoesNotContain("Заявки комплексу", telegram.Messages[^1].Text);
    }
}
