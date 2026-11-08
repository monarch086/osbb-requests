using System.Net;
using System.Text.Json;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class MainKeyboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainKeyboardHasEveryExistingMenuItemForTheCorrectUser(bool registered)
    {
        using var capture = new Capture();
        using var http = new HttpClient(capture);
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(42, "Меню", default,
            registered ? ReplyKeyboard.MainResident : ReplyKeyboard.MainGuest);
        using var json = JsonDocument.Parse(capture.Body!);
        var markup = json.RootElement.GetProperty("reply_markup");
        Assert.True(markup.GetProperty("is_persistent").GetBoolean());
        Assert.False(markup.GetProperty("one_time_keyboard").GetBoolean());
        var labels = markup.GetProperty("keyboard").EnumerateArray().SelectMany(row => row.EnumerateArray())
            .Select(button => button.GetProperty("text").GetString()).ToArray();
        Assert.Equal(CommandMenu.ForResident(registered).Select(command => command.Description), labels);
    }

    [Theory]
    [InlineData("Власні заявки", "/my")]
    [InlineData("Переглянути власні заявки", "/my")]
    [InlineData("Довідка: контакти диспетчерів", "/contacts")]
    [InlineData("Контакти майстрів", "/contacts")]
    [InlineData("Деталі заявки: /request номер", "/request")]
    [InlineData("Деталі заявки", "/request")]
    public async Task ButtonAndCommandProduceEquivalentResults(string button, string command)
    {
        var handler = new CommandHandler(new MemoryRepository(), "Bot", "building");
        var result = await handler.HandleAsync(new(1, 42, 42, button), default);
        var expected = await handler.HandleAsync(new(1, 42, 42, command), default);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Змінити профіль", "edit_profile")]
    [InlineData("Змінити інформацію про себе", "edit_profile")]
    [InlineData("Видалити профіль", "delete_profile")]
    [InlineData("Видалити інформацію про себе", "delete_profile")]
    public void ProfileButtonNamesKeepRegisteredOnlyAccess(string label, string command)
    {
        Assert.Equal(command, CommandMenu.CommandForButton(label, true));
        Assert.Null(CommandMenu.CommandForButton(label, false));
    }

    [Fact]
    public async Task ButtonsDoNotStealTextInActiveRequestAndMenuReturnsAfterCancel()
    {
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, new CommandHandler(repository, "Bot", "building"), telegram, "building");
        await processor.ProcessAsync(new(1, 42, 42, "/new"), default);
        await processor.ProcessAsync(new(2, 42, 42, "4Б"), default);
        await processor.ProcessAsync(new(3, 42, 42, "1"), default);
        await processor.ProcessAsync(new(4, 42, 42, "Інше"), default);
        Assert.Equal(ReplyKeyboard.Remove, telegram.Keyboards[^1]);
        await processor.ProcessAsync(new(5, 42, 42, "Довідка: контакти диспетчерів"), default);
        Assert.Equal("Довідка: контакти диспетчерів", Assert.Single(repository.Requests).Description);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
        await processor.ProcessAsync(new(6, 42, 42, "/new"), default);
        await processor.ProcessAsync(new(7, 42, 42, "/cancel"), default);
        Assert.Equal(ReplyKeyboard.MainResident, telegram.Keyboards[^1]);
    }

    [Fact]
    public async Task GuestMenuIgnoresStaleDialogsFromBeforeAccessWasRemoved()
    {
        var repo = new MemoryRepository();
        repo.Residents.Remove(42);
        repo.Drafts[42] = new(42, "building", RequestDraftStage.Description, 1);
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repo, new CommandHandler(repo, "Bot", "building"), telegram, "building");
        await processor.ProcessAsync(new(1, 42, 42, "Довідка: контакти диспетчерів"), default);
        Assert.Contains("Телефони диспетчерів", telegram.Messages[^1].Text);
        Assert.Equal(ReplyKeyboard.MainGuest, telegram.Keyboards[^1]);
    }

    [Fact]
    public async Task GuestCannotUseResidentOnlyButtons()
    {
        var repo = new MemoryRepository();
        repo.Residents.Remove(42);
        var outcome = await new CommandHandler(repo, "Bot", "building").HandleAsync(new(1, 42, 42, "Мій особовий рахунок"), default);
        Assert.Contains("реєстрацію", outcome.Reply);
        Assert.Null(outcome.Profile);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
        }
    }
}
