using OsbbRequests.CommandProcessor.Notifications;
using System.Net;
using System.Text.Json;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Telegram;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static MaintenanceRequest Request() => new(7, 42, "complex", "інше", "", "Опис заявки", Now,
        Scope: RequestScope.Building, BuildingNumber: "4Б", Floor: 12);
    private static NotificationWorkItem Work(string id = "7-v2", int attempts = 0) => new(id,
        new(id, "complex", 42, 7, 2, "Оновлення вашої заявки №7", Now), attempts);

    [Fact]
    public async Task ApprovalNotificationRetriesThenDeliversOnlyToApprovedResident()
    {
        var registration = new OsbbRequests.Domain.Models.ResidentRegistration(new string('a', 32), 99, "complex", RegistrationStage.Approved, 5,
            Now.AddDays(-1), Now, "+380500000000", "Олена", "Тестова");
        var notification = ResidentNotification.RegistrationApproved(registration);
        var repository = new MemoryNotifications(new NotificationWorkItem(notification.Id, notification, 0));
        var telegram = new NotificationTelegram { Error = new InvalidOperationException("HTTP 503") };
        var processor = new NotificationProcessor(repository, telegram, timeProvider: new FixedClock());
        await processor.RunAsync(default);
        Assert.Single(repository.Retries);
        Assert.Empty(repository.Results);
        telegram.Error = null;
        await processor.RunAsync(default);
        await processor.RunAsync(default);
        var message = Assert.Single(telegram.Sent);
        Assert.Equal(99, message.ChatId);
        Assert.Contains("Вашу реєстрацію в ЖК «Зелений острів 2» схвалено.", message.Text);
        Assert.Contains("20.10.2026 13:00 (Київ)", message.Text);
        Assert.Equal("Delivered", Assert.Single(repository.Results).Disposition);
    }

    [Fact]
    public void ExistingRequestNotificationPayloadStillDeserializes()
    {
        const string payload = """
            {"Id":"7-v2","ComplexId":"complex","OwnerId":42,"RequestId":7,"RequestVersion":2,"Text":"Оновлення заявки","CreatedAt":"2026-10-20T10:00:00+00:00"}
            """;
        var notification = JsonSerializer.Deserialize<ResidentNotification>(payload)!;
        Assert.Equal(7, notification.RequestId);
        Assert.Equal(2, notification.RequestVersion);
        Assert.Equal(42, notification.OwnerId);
    }

    [Theory]
    [InlineData(RequestStatus.InProgress, "В роботі")]
    [InlineData(RequestStatus.AwaitingClarification, "Очікує уточнення")]
    [InlineData(RequestStatus.Completed, "Виконана")]
    [InlineData(RequestStatus.Rejected, "Відхилена")]
    public void StatusChangeCreatesOwnerNotificationWithUkrainianStatusAndDate(RequestStatus status, string label)
    {
        var old = Request();
        var notification = ResidentNotification.Create(old, old with { Status = status, Resolution = "Пояснення" }, 5, Now, "Пояснення")!;
        Assert.Equal(42, notification.OwnerId);
        Assert.Equal("complex", notification.ComplexId);
        Assert.Equal("7-v5", notification.Id);
        Assert.Contains("Нова → " + label, notification.Text);
        Assert.Contains("20.10.2026 13:00 (Київ)", notification.Text);
        Assert.Contains("Пояснення для мешканця:\nПояснення", notification.Text);
        Assert.Contains("/request 7", notification.Text);
        Assert.DoesNotContain("admin@example", notification.Text);
    }

    [Theory]
    [InlineData("Попередній", "Новий коментар")]
    [InlineData("Коментар", "Коментар")]
    public void NewCommentNotifiesEvenWhenIdenticalToPrevious(string previous, string comment)
    {
        var old = Request() with { Resolution = previous };
        var result = ResidentNotification.Create(old, old with { Resolution = comment }, 2, Now, comment)!;
        Assert.DoesNotContain("→", result.Text);
        Assert.Contains(comment, result.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnchangedStatusWithoutNewCommentDoesNotCreateNotification(string? comment)
    {
        var old = Request() with { Resolution = "Попереднє пояснення" };
        Assert.Null(ResidentNotification.Create(old, old with { Priority = "High" }, 2, Now, comment));
    }

    [Fact]
    public void CannotRedirectNotificationToAnotherOwnerOrComplex()
    {
        var old = Request();
        Assert.Throws<InvalidOperationException>(() => ResidentNotification.Create(old, old with { OwnerId = 99 }, 2, Now));
        Assert.Throws<InvalidOperationException>(() => ResidentNotification.Create(old, old with { BuildingId = "other" }, 2, Now));
    }

    [Fact]
    public void MaximumCommentFitsSinglePlainTextTelegramMessage()
    {
        var old = Request();
        var result = ResidentNotification.Create(old, old with { Status = RequestStatus.Completed, Resolution = "<b>" + new string('О', 993) + "</b>" }, 2, Now, "<b>" + new string('О', 993) + "</b>")!;
        Assert.True(result.Text.Length <= 4096);
        Assert.Contains("<b>", result.Text);
    }

    [Fact]
    public async Task DeliversOnlyToAuthorAndCompletedJobIsNotSentAgain()
    {
        var repository = new MemoryNotifications(Work());
        var telegram = new NotificationTelegram();
        var processor = new NotificationProcessor(repository, telegram, timeProvider: new FixedClock());
        await processor.RunAsync(default);
        await processor.RunAsync(default);
        var message = Assert.Single(telegram.Sent);
        Assert.Equal(42, message.ChatId);
        Assert.Contains("№7", message.Text);
        Assert.Equal("Delivered", Assert.Single(repository.Results).Disposition);
    }

    [Fact]
    public async Task ResidentApprovalUpdatesChatMenuBeforeSendingConfirmation()
    {
        var registration = new OsbbRequests.Domain.Models.ResidentRegistration("reference", 42, "complex", RegistrationStage.Approved,
            5, Now, Now);
        var work = Work() with { Notification = ResidentNotification.RegistrationApproved(registration) };
        var repository = new MemoryNotifications(work);
        var telegram = new NotificationTelegram();
        telegram.BeforeSend = () => Assert.Equal((42L, true), Assert.Single(telegram.Menus));
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Single(telegram.Sent);
        Assert.Equal("Delivered", Assert.Single(repository.Results).Disposition);
    }

    [Fact]
    public async Task AdministratorAlertsDoNotOverwritePrivateResidentMenus()
    {
        var work = Work() with { Notification = Work().Notification with { AdministratorId = "admin", AdministratorEvent = AdministratorEvent.NewRequest } };
        var repository = new MemoryNotifications(work);
        var telegram = new NotificationTelegram();
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Empty(telegram.Menus);
        Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task BroadcastIsExpandedWithoutSendingToPlaceholderChat()
    {
        var broadcast = Work() with { Notification = Work().Notification with { OwnerId = 0, AdministratorEvent = AdministratorEvent.NewRequest } };
        var repository = new MemoryNotifications(broadcast);
        var telegram = new NotificationTelegram();
        var processor = new NotificationProcessor(repository, telegram, timeProvider: new FixedClock());
        await processor.RunAsync(default);
        await processor.RunAsync(default);
        Assert.Equal(1, repository.Expansions);
        Assert.Empty(telegram.Sent);
    }

    [Fact]
    public async Task UnsubscribedRecipientIsSkippedWithoutTelegramMessage()
    {
        var repository = new MemoryNotifications(Work()) { AllowDelivery = false };
        var telegram = new NotificationTelegram();
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Empty(telegram.Sent);
        Assert.Equal("Skipped", Assert.Single(repository.Results).Disposition);
        Assert.Empty(telegram.Menus);
    }

    [Fact]
    public async Task FailedDeliveryLogsOnlyEventIdAndErrorType()
    {
        var repository = new MemoryNotifications(Work());
        var telegram = new NotificationTelegram { Error = new InvalidOperationException("SECRET private payload") };
        var logs = new List<string>();
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default, logs.Add);
        var message = Assert.Single(logs);
        Assert.Contains("notificationId=7-v2", message);
        Assert.Contains("errorType=InvalidOperationException", message);
        Assert.DoesNotContain("SECRET", message);
        Assert.DoesNotContain("Оновлення", message);
    }

    [Fact]
    public async Task DeliveryAcknowledgementFailureKeepsDurableJobForRetry()
    {
        var repository = new MemoryNotifications(Work()) { FailComplete = true };
        var telegram = new NotificationTelegram();
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Single(telegram.Sent);
        Assert.Empty(repository.Results);
        Assert.Single(repository.Retries);
        Assert.Single(repository.Items);
    }

    [Fact]
    public async Task ActiveLeaseSkipsDelivery()
    {
        var repository = new MemoryNotifications(Work()) { CanClaim = false };
        var telegram = new NotificationTelegram();
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Empty(telegram.Sent);
        Assert.Empty(repository.Results);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(3, 480)]
    [InlineData(9, 3600)]
    public async Task TemporaryDeliveryFailureRetainsJobAndSchedulesBackoff(int attempts, int delay)
    {
        var repository = new MemoryNotifications(Work(attempts: attempts));
        var telegram = new NotificationTelegram { Error = new InvalidOperationException("HTTP 503") };
        var notifier = new RecordingServiceNotifier(fail: true);
        await new NotificationProcessor(repository, telegram, notifier, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Empty(repository.Results);
        Assert.Equal(Now.AddSeconds(delay), Assert.Single(repository.Retries).Due);
        Assert.Single(repository.Items);
        Assert.Equal("deliver_notification", Assert.Single(notifier.Errors).Operation);
    }

    [Fact]
    public async Task NotificationFailureLogsSafeCategoryAndStatusWhileRetrying()
    {
        var repository = new MemoryNotifications(Work());
        var telegram = new NotificationTelegram { Error = new TelegramApiException(TelegramFailureCategory.HttpError, 503) };
        var logs = new List<string>();
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default, logs.Add);
        var log = Assert.Single(logs);
        Assert.Contains("telegramFailure=HttpError", log);
        Assert.Contains("telegramHttpStatus=503", log);
        Assert.Single(repository.Retries);
        Assert.Empty(repository.Results);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public async Task UnavailableRecipientDoesNotBlockRemainingNotifications(int status)
    {
        var repository = new MemoryNotifications(Work(), Work("8-v2"));
        var telegram = new NotificationTelegram { FailFirst = true, Error = new TelegramRecipientUnavailableException(status) };
        var notifier = new RecordingServiceNotifier(fail: true);
        await new NotificationProcessor(repository, telegram, notifier, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Equal(new[] { "Undeliverable", "Delivered" }, repository.Results.Select(r => r.Disposition));
        Assert.Single(telegram.Sent);
        Assert.Empty(repository.Retries);
        Assert.Equal("notification_undeliverable", Assert.Single(notifier.Errors).Operation);
    }

    [Fact]
    public async Task RateLimitPausesWholeSenderAcrossInvocationsUntilRetryAfter()
    {
        var repository = new MemoryNotifications(Work(), Work("8-v2"));
        var telegram = new NotificationTelegram { Error = new TelegramRateLimitException(400) };
        var processor = new NotificationProcessor(repository, telegram, timeProvider: new FixedClock());
        await processor.RunAsync(default);
        await processor.RunAsync(default);
        Assert.Equal(Now.AddSeconds(400), repository.PausedUntil);
        Assert.Equal(Now.AddSeconds(400), Assert.Single(repository.Retries).Due);
        Assert.Equal(1, telegram.Attempts);
    }

    [Fact]
    public async Task CancellationLeavesLeasedJobForRecoveryWithoutAcknowledgingDelivery()
    {
        using var cts = new CancellationTokenSource();
        var repository = new MemoryNotifications(Work());
        var telegram = new NotificationTelegram { BeforeSend = () => cts.Cancel(), Error = new OperationCanceledException() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NotificationProcessor(repository, telegram).RunAsync(cts.Token));
        Assert.Empty(repository.Results);
        Assert.Empty(repository.Retries);
        Assert.Single(repository.Items);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TelegramClientClassifiesPermanentRecipientFailuresWithoutExposingResponse(HttpStatusCode status)
    {
        using var http = new HttpClient(new ResponseHandler(status));
        var error = await Assert.ThrowsAsync<TelegramRecipientUnavailableException>(() => new TelegramClient(http, new FakeSecret("SECRET"))
            .SendMessageAsync(42, "Текст", default));
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NotificationTelegram : ITelegramClient
    {
        public List<(long ChatId, bool Registered)> Menus { get; } = [];
        public Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken, bool force = false)
        { Menus.Add((chatId, registered)); return Task.CompletedTask; }
        public List<(long ChatId, string Text)> Sent { get; } = [];
        public Exception? Error;
        public bool FailFirst;
        public Action? BeforeSend;
        public int Attempts;
        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
        {
            Attempts++;
            BeforeSend?.Invoke();
            if (Error is not null && (!FailFirst || Attempts == 1)) throw Error;
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }
    }
    private sealed class MemoryNotifications(params NotificationWorkItem[] items) : INotificationRepository
    {
        public Task ExpandAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct) { Expansions++; Items.Remove(item); return Task.CompletedTask; }
        public int Expansions;
        public Task<bool> CanDeliverAsync(NotificationWorkItem item, CancellationToken ct) => Task.FromResult(AllowDelivery);
        public bool AllowDelivery = true;
        public List<NotificationWorkItem> Items = [.. items];
        public List<(NotificationWorkItem Item, string Disposition)> Results = [];
        public List<(NotificationWorkItem Item, DateTimeOffset Due)> Retries = [];
        public DateTimeOffset? PausedUntil;
        public bool CanClaim = true;
        public bool FailComplete;
        public Task<IReadOnlyList<NotificationWorkItem>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<NotificationWorkItem>>(PausedUntil > now ? [] : Items.ToArray());
        }
        public Task<bool> ClaimAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct) => Task.FromResult(CanClaim);
        public Task CompleteAsync(NotificationWorkItem item, string lease, string disposition, DateTimeOffset now, CancellationToken ct)
        { if (FailComplete) throw new InvalidOperationException("Database unavailable"); Items.Remove(item); Results.Add((item, disposition)); return Task.CompletedTask; }
        public Task RetryAsync(NotificationWorkItem item, string lease, DateTimeOffset dueAt, CancellationToken ct)
        { Retries.Add((item, dueAt)); return Task.CompletedTask; }
        public Task PauseAsync(DateTimeOffset until, CancellationToken ct) { PausedUntil = until; return Task.CompletedTask; }
    }
    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("sensitive-response") });
    }
}
