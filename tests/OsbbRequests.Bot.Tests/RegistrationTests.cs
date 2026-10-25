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
            new CommandHandler(repository, "OsbbBot", "building", residentApprovalMode: ResidentApprovalMode.Manual), telegram));
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
        Assert.Equal(RegistrationStage.AwaitingBuilding, repository.Registrations[99].Stage);
        await processor.ProcessAsync(Message(5, "Будинок 4В"), CancellationToken.None);
        Assert.Equal(RegistrationStage.AwaitingApartment, repository.Registrations[99].Stage);
        await processor.ProcessAsync(Message(6, "125"), CancellationToken.None);
        var registration = repository.Registrations[99];
        Assert.Equal("4В", registration.BuildingNumber);
        Assert.Equal("125", registration.ApartmentNumber);
        Assert.Equal("+380671234567", registration.Phone);
        Assert.Equal("Олена", registration.FirstName);
        Assert.Equal("Ковальчук", registration.LastName);
        Assert.Equal(RegistrationStage.Pending, registration.Stage);
        Assert.DoesNotContain(99, repository.Residents);
        await processor.ProcessAsync(Message(7, "/new 4В | 12 | сантехніка | двір | Протікає труба"), CancellationToken.None);
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
        await processor.ProcessAsync(Message(6, "4Г"), CancellationToken.None);
        await processor.ProcessAsync(Message(7, "1"), CancellationToken.None);
        var before = repository.Registrations[99];
        await processor.ProcessAsync(Message(8, "/register"), CancellationToken.None);
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
        await processor.ProcessAsync(Message(2, "/new 4В | 12 | сантехніка | двір | Протікає труба"), CancellationToken.None);
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

    [Theory]
    [InlineData("4Б")]
    [InlineData("4В")]
    [InlineData("4Г")]
    [InlineData("4Д")]
    public async Task BuildingSelectionResumesAndDoesNotAdvanceTwiceOnDuplicateUpdate(string building)
    {
        var (repository, _, processor) = Create();
        repository.Registrations[99] = new("reference", 99, "building", RegistrationStage.AwaitingBuilding, 4,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "+380671234567", "Олена", "Ковальчук");
        await processor.ProcessAsync(Message(1, "/start"), default);
        Assert.Equal(ReplyKeyboard.RegistrationBuilding, repository.Replies[1].Keyboard);
        var selection = Message(2, "Будинок " + building);
        await processor.ProcessAsync(selection, default);
        await processor.ProcessAsync(selection, default);
        Assert.Equal(building, repository.Registrations[99].BuildingNumber);
        Assert.Equal(5, repository.Registrations[99].Version);
        Assert.Equal(RegistrationStage.AwaitingApartment, repository.Registrations[99].Stage);
        await processor.ProcessAsync(Message(3, "/start"), default);
        Assert.Contains("квартири", repository.Replies[3].Text);
        await processor.ProcessAsync(Message(4, "00125"), default);
        Assert.Equal("125", repository.Registrations[99].ApartmentNumber);
        Assert.Equal(RegistrationStage.Pending, repository.Registrations[99].Stage);
        Assert.Equal(6, repository.Registrations[99].Version);
    }

    [Theory]
    [InlineData("4А")]
    [InlineData("4B")]
    [InlineData("1")]
    [InlineData("Весь комплекс")]
    public async Task InvalidResidentialBuildingCannotAdvanceRegistration(string text)
    {
        var (repository, _, processor) = Create();
        repository.Registrations[99] = new("reference", 99, "building", RegistrationStage.AwaitingBuilding, 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await processor.ProcessAsync(Message(1, text), default);
        Assert.Equal(RegistrationStage.AwaitingBuilding, repository.Registrations[99].Stage);
        Assert.Null(repository.Registrations[99].BuildingNumber);
        Assert.Equal(4, repository.Registrations[99].Version);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("квартира")]
    [InlineData("2147483648")]
    public async Task InvalidApartmentCannotSubmitRegistration(string text)
    {
        var (repository, _, processor) = Create();
        repository.Registrations[99] = new("reference", 99, "building", RegistrationStage.AwaitingApartment, 5,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, BuildingNumber: "4Д");
        await processor.ProcessAsync(Message(1, text), default);
        Assert.Equal(RegistrationStage.AwaitingApartment, repository.Registrations[99].Stage);
        Assert.Null(repository.Registrations[99].ApartmentNumber);
    }

    [Fact]
    public async Task CancelClearsResidentialAddressAndRemovesBuildingKeyboard()
    {
        var (repository, _, processor) = Create();
        repository.Registrations[99] = new("reference", 99, "building", RegistrationStage.AwaitingApartment, 5,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, BuildingNumber: "4Д", ApartmentNumber: "125");
        await processor.ProcessAsync(Message(1, "/cancel"), default);
        Assert.Null(repository.Registrations[99].BuildingNumber);
        Assert.Null(repository.Registrations[99].ApartmentNumber);
        Assert.Equal(RegistrationStage.Cancelled, repository.Registrations[99].Stage);
        Assert.Equal(ReplyKeyboard.Remove, repository.Replies[1].Keyboard);
    }

    [Fact]
    public void OldProfileHasNoResidentialAddressAndOldEnumValuesRemainCompatible()
    {
        var old = JsonSerializer.Deserialize<ResidentRegistration>("{\"Id\":\"old\",\"UserId\":99,\"BuildingId\":\"building\",\"Stage\":\"Approved\",\"Version\":4}")!;
        Assert.Null(old.BuildingNumber);
        Assert.Null(old.ApartmentNumber);
        Assert.Equal(3, (int)RegistrationStage.Pending);
        Assert.Equal(4, (int)RegistrationStage.Approved);
    }

    [Fact]
    public async Task ResidentialBuildingKeyboardContainsOnlyActualBuildings()
    {
        using var handler = new CaptureHttp();
        using var http = new HttpClient(handler);
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(99, "Будинок", default, ReplyKeyboard.RegistrationBuilding);
        using var json = JsonDocument.Parse(handler.Sent!);
        var buttons = json.RootElement.GetProperty("reply_markup").GetProperty("keyboard").EnumerateArray()
            .SelectMany(row => row.EnumerateArray()).Select(button => button.GetProperty("text").GetString()).ToArray();
        Assert.Equal(new[] { "Будинок 4Б", "Будинок 4В", "Будинок 4Г", "Будинок 4Д" }, buttons);
    }

    [Fact]
    public async Task StartAloneRestartsRejectedRegistrationAndFinishesTheManualFlow()
    {
        var (repository, telegram, processor) = Create();
        repository.Registrations[99] = new("old", 99, "building", RegistrationStage.Rejected, 7,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DecisionReason: "Уточніть дані");
        await processor.ProcessAsync(Message(1, "/start"), default);
        Assert.Equal(RegistrationStage.AwaitingPhone, repository.Registrations[99].Stage);
        Assert.Equal(ReplyKeyboard.RequestContact, repository.Replies[1].Keyboard);
        Assert.DoesNotContain("/register", telegram.Messages[^1].Text);
        await processor.ProcessAsync(Message(2, contact: new("+380671234567", 99)), default);
        await processor.ProcessAsync(Message(3, "Олена"), default);
        await processor.ProcessAsync(Message(4, "Ковальчук"), default);
        await processor.ProcessAsync(Message(5, "4Д"), default);
        await processor.ProcessAsync(Message(6, "125"), default);
        Assert.Equal(RegistrationStage.Pending, repository.Registrations[99].Stage);
        Assert.DoesNotContain(99, repository.Residents);
    }

    [Fact]
    public async Task HelpShowsStartWithoutObsoleteCommandDescriptions()
    {
        var (_, telegram, processor) = Create();
        await processor.ProcessAsync(Message(1, "/help"), default);
        Assert.Contains("/start", telegram.Messages[^1].Text);
        Assert.DoesNotContain("/register", telegram.Messages[^1].Text);
        Assert.DoesNotContain("/categories", telegram.Messages[^1].Text);
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
