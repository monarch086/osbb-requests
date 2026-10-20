using OsbbRequests.Shared.Models;
using System.Net;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Bot.Webhook;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RegistrationTests
{
    private static CommandEnvelope Message(long id, string text = "", TelegramContact? contact = null) => new(id, 99, 99, text, contact);
    private static (MemoryRepository Repository, FakeTelegram Telegram, CommandProcessor Processor) Create()
    {
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram();
        return (repository, telegram, new CommandProcessor(repository,
            new CommandHandler(repository, "OsbbBot", "building"), telegram));
    }

    [Fact]
    public async Task StartRequestsContactWithoutExposingTelegramId()
    {
        var (repository, telegram, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingPhone, repository.Registrations[99].Stage);
        Assert.Equal(ReplyKeyboard.RequestContact, repository.Replies[1].Keyboard);
        Assert.DoesNotContain("Telegram ID", Assert.Single(telegram.Messages).Text);
        Assert.DoesNotContain(99, repository.Residents);
    }

    [Fact]
    public async Task FullRegistrationBecomesPendingAndDoesNotGrantAccess()
    {
        var (repository, telegram, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        await processor.ProcessAsync(Message(2, contact: new TelegramContact("380 (67) 123-45-67", 99)), CancellationToken.None);
        Assert.Equal(ReplyKeyboard.Remove, repository.Replies[2].Keyboard);
        await processor.ProcessAsync(Message(3, "Олена"), CancellationToken.None);
        await processor.ProcessAsync(Message(4, "Ковальчук"), CancellationToken.None);
        var registration = repository.Registrations[99];
        Assert.Equal("+380671234567", registration.Phone);
        Assert.Equal("Олена", registration.FirstName);
        Assert.Equal("Ковальчук", registration.LastName);
        Assert.Equal(RegistrationStage.Pending, registration.Stage);
        Assert.DoesNotContain(99, repository.Residents);
        await processor.ProcessAsync(Message(5, "/new сантехніка | двір | Протікає труба"), CancellationToken.None);
        Assert.Empty(repository.Requests);
        Assert.Contains("очікує", telegram.Messages[^1].Text);
        Assert.DoesNotContain("Telegram ID", telegram.Messages[^1].Text);
    }

    [Theory]
    [InlineData("+380671234567", 100L)]
    [InlineData("+380671234567", null)]
    [InlineData("not-a-phone", 99L)]
    [InlineData("+123", 99L)]
    public async Task RejectsOtherPersonsOrInvalidContact(string phone, long? contactOwner)
    {
        var (repository, _, processor) = Create();
        await processor.ProcessAsync(Message(1, "/register"), CancellationToken.None);
        await processor.ProcessAsync(Message(2, contact: new TelegramContact(phone, contactOwner)), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingPhone, repository.Registrations[99].Stage);
        Assert.Null(repository.Registrations[99].Phone);
        Assert.Equal(ReplyKeyboard.RequestContact, repository.Replies[2].Keyboard);
    }

    [Fact]
    public async Task TypedPhoneIsNotAcceptedAsVerifiedContact()
    {
        var (repository, _, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        await processor.ProcessAsync(Message(2, "+380671234567"), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingPhone, repository.Registrations[99].Stage);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("<script>")]
    [InlineData("Олена\nКовальчук")]
    public async Task RejectsInvalidName(string name)
    {
        var (repository, _, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        await processor.ProcessAsync(Message(2, contact: new TelegramContact("+380671234567", 99)), CancellationToken.None);
        await processor.ProcessAsync(Message(3, name), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingFirstName, repository.Registrations[99].Stage);
    }

    [Fact]
    public async Task DuplicateContactAndRetryDoNotAdvanceTwoSteps()
    {
        var (repository, telegram, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        var contact = Message(2, contact: new TelegramContact("+380671234567", 99));
        telegram.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(contact, CancellationToken.None));
        Assert.Equal(RegistrationStage.AwaitingFirstName, repository.Registrations[99].Stage);
        telegram.Fail = false;
        await processor.ProcessAsync(contact, CancellationToken.None);
        await processor.ProcessAsync(contact, CancellationToken.None);
        Assert.Equal(2, repository.Registrations[99].Version);
        Assert.Equal(RegistrationStage.AwaitingFirstName, repository.Registrations[99].Stage);
        Assert.Equal(2, telegram.Messages.Count);
    }

    [Fact]
    public async Task StartResumesDraftAndPendingRegistrationCannotBeReset()
    {
        var (repository, telegram, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        await processor.ProcessAsync(Message(2, contact: new TelegramContact("+380671234567", 99)), CancellationToken.None);
        await processor.ProcessAsync(Message(3, "/start"), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingFirstName, repository.Registrations[99].Stage);
        await processor.ProcessAsync(Message(4, "Анна-Марія"), CancellationToken.None);
        await processor.ProcessAsync(Message(5, "О’Коннор"), CancellationToken.None);
        var before = repository.Registrations[99];
        await processor.ProcessAsync(Message(6, "/register"), CancellationToken.None);
        Assert.Equal(before, repository.Registrations[99]);
        Assert.Contains("очікує", telegram.Messages[^1].Text);
    }

    [Fact]
    public async Task CancelClearsDraftAndRemovesKeyboard()
    {
        var (repository, _, processor) = Create();
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        await processor.ProcessAsync(Message(2, contact: new TelegramContact("+380671234567", 99)), CancellationToken.None);
        await processor.ProcessAsync(Message(3, "/cancel"), CancellationToken.None);
        Assert.Equal(RegistrationStage.Cancelled, repository.Registrations[99].Stage);
        Assert.Null(repository.Registrations[99].Phone);
        Assert.Equal(ReplyKeyboard.Remove, repository.Replies[3].Keyboard);
    }

    [Fact]
    public async Task ApprovedResidentCanCreateRequestsAndStartDoesNotExposeInternalId()
    {
        var (repository, telegram, processor) = Create();
        repository.Residents.Add(99);
        await processor.ProcessAsync(Message(1, "/start"), CancellationToken.None);
        Assert.DoesNotContain("Telegram ID", Assert.Single(telegram.Messages).Text);
        Assert.Empty(repository.Registrations);
        await processor.ProcessAsync(Message(2, "/new сантехніка | двір | Протікає труба"), CancellationToken.None);
        Assert.Single(repository.Requests);
    }

    [Fact]
    public async Task RejectedApplicantSeesReasonAndCanRegisterAgain()
    {
        var (repository, telegram, processor) = Create();
        await processor.ProcessAsync(Message(1, "/register"), CancellationToken.None);
        var current = repository.Registrations[99];
        repository.Registrations[99] = current with
        {
            Stage = RegistrationStage.Rejected,
            DecisionReason = "Уточніть прізвище",
            Version = current.Version + 1
        };
        await processor.ProcessAsync(Message(2, "/start"), CancellationToken.None);
        Assert.Contains("Уточніть прізвище", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(3, "/register"), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingPhone, repository.Registrations[99].Stage);
        Assert.NotEqual(current.Id, repository.Registrations[99].Id);
    }

    [Fact]
    public async Task WebhookAcceptsPrivateContactWithoutText()
    {
        var queue = new FakeQueue();
        var payload = JsonSerializer.Serialize(new
        {
            update_id = 100,
            message = new
            {
                from = new { id = 99, is_bot = false },
                chat = new { id = 99, type = "private" },
                contact = new { user_id = 99, phone_number = "+380671234567" }
            }
        });
        var response = await new WebhookHandler(new FakeSecret("secret"), queue, "OsbbBot").HandleAsync(
            new APIGatewayHttpApiV2ProxyRequest
            {
                Headers = new Dictionary<string, string> { ["x-telegram-bot-api-secret-token"] = "secret" },
                Body = payload
            }, CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        var command = Assert.Single(queue.Commands);
        Assert.Equal(99, command.Contact?.UserId);
        Assert.Equal("+380671234567", command.Contact?.PhoneNumber);
    }

    [Fact]
    public async Task ContactKeyboardUsesTelegramRequestContact()
    {
        using var handler = new CaptureHttp();
        using var http = new HttpClient(handler);
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(99, "Телефон", CancellationToken.None,
            ReplyKeyboard.RequestContact);
        using var body = JsonDocument.Parse(handler.Sent!);
        var markup = body.RootElement.GetProperty("reply_markup");
        Assert.True(markup.GetProperty("keyboard")[0][0].GetProperty("request_contact").GetBoolean());
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(99, "Ім’я", CancellationToken.None,
            ReplyKeyboard.Remove);
        using var removed = JsonDocument.Parse(handler.Sent!);
        Assert.True(removed.RootElement.GetProperty("reply_markup").GetProperty("remove_keyboard").GetBoolean());
    }

    private sealed class CaptureHttp : HttpMessageHandler
    {
        public string? Sent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
