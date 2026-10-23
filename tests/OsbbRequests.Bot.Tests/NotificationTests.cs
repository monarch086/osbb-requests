using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Bot;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Notifications;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly AdminUser Admin = new("admin", "admin@example.invalid", "Адміністратор", true, false, 1);
    private static MaintenanceRequest Request() => new(7, 42, "complex", "інше", "", "Опис заявки", Now,
        Scope: RequestScope.Building, BuildingNumber: "4Б", Floor: 12);
    private static NotificationWorkItem Work(string id = "7-v2", int attempts = 0) => new(ResidentNotification.DueKey(Now, id),
        new(id, "complex", 42, 7, 2, "Оновлення вашої заявки №7", Now), attempts);

    [Fact]
    public async Task ApprovalNotificationRetriesThenDeliversOnlyToApprovedResident()
    {
        var registration = new OsbbRequests.Shared.Models.ResidentRegistration(new string('a', 32), 99, "complex", RegistrationStage.Approved, 5,
            Now.AddDays(-1), Now, "+380500000000", "Олена", "Тестова");
        var notification = ResidentNotification.RegistrationApproved(registration);
        var repository = new MemoryNotifications(new NotificationWorkItem(ResidentNotification.DueKey(Now, notification.Id), notification, 0));
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
        var notification = ResidentNotification.Create(old, old with { Status = status, Resolution = "Пояснення" }, 5, Now)!;
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
    [InlineData(null, "Новий коментар")]
    [InlineData("Попередній", "Виправлений")]
    [InlineData("Попередній", null)]
    public void CommentChangeNotifiesWithoutStatusChange(string? previous, string? next)
    {
        var old = Request() with { Resolution = previous };
        var result = ResidentNotification.Create(old, old with { Resolution = next }, 2, Now)!;
        Assert.DoesNotContain("→", result.Text);
        Assert.Contains(next ?? "видалено", result.Text);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Коментар", " Коментар ")]
    [InlineData("Коментар", "Коментар")]
    public void UnchangedStatusAndCommentDoNotCreateNotification(string? previous, string? next)
    {
        var old = Request() with { Resolution = previous };
        Assert.Null(ResidentNotification.Create(old, old with { Resolution = next, Priority = "High", AssignedTo = "admin", Visibility = RequestVisibility.Public }, 2, Now));
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
        var result = ResidentNotification.Create(old, old with { Status = RequestStatus.Completed, Resolution = "<b>" + new string('О', 993) + "</b>" }, 2, Now)!;
        Assert.True(result.Text.Length <= 4096);
        Assert.Contains("<b>", result.Text);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AdminUpdateWritesOneOutboxRecordAtomicallyWithRequestAndAudit(bool statusChanged, bool commentChanged)
    {
        using var db = new RecordingDynamo();
        var old = Request();
        var next = old with { Status = statusChanged ? RequestStatus.InProgress : old.Status, Resolution = commentChanged ? "Уточніть проблему" : null };
        await new DynamoAdminStore(db, "admins", "data", "complex").UpdateRequestAsync(Admin, new(old, 4), next, default);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(statusChanged || commentChanged ? 4 : 3, writes.Count);
        Assert.Single(writes, w => w.ConditionCheck is not null); // Active administrator checked in same transaction.
        Assert.Equal("4", Assert.Single(writes, w => w.Update is not null).Update.ExpressionAttributeValues[":expected"].N);
        if (!statusChanged && !commentChanged) return;
        var pending = Assert.Single(writes, w => w.Put?.Item["pk"].S == "NOTIFICATIONS#complex").Put;
        var job = JsonSerializer.Deserialize<ResidentNotification>(pending.Item["payload"].S)!;
        Assert.Equal(42, job.OwnerId);
        Assert.Equal("7-v5", job.Id);
        Assert.Equal("0", pending.Item["attempts"].N);
        Assert.False(pending.Item.ContainsKey("expiresAt"));
        Assert.Equal("attribute_not_exists(pk)", pending.ConditionExpression);
        Assert.StartsWith("DUE#", pending.Item["sk"].S);
    }

    [Fact]
    public async Task ConflictingAdminUpdateDoesNotIndependentlyEnqueueNotification()
    {
        using var db = new RecordingDynamo { Conflict = true };
        var old = Request();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .UpdateRequestAsync(Admin, new(old, 4), old with { Status = RequestStatus.InProgress }, default));
        Assert.Equal(409, error.Status);
        Assert.Equal(1, db.TransactionCalls);
        Assert.Equal(4, db.Transaction!.TransactItems.Count);
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
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Empty(repository.Results);
        Assert.Equal(Now.AddSeconds(delay), Assert.Single(repository.Retries).Due);
        Assert.Single(repository.Items);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public async Task UnavailableRecipientDoesNotBlockRemainingNotifications(int status)
    {
        var repository = new MemoryNotifications(Work(), Work("8-v2"));
        var telegram = new NotificationTelegram { FailFirst = true, Error = new TelegramRecipientUnavailableException(status) };
        await new NotificationProcessor(repository, telegram, timeProvider: new FixedClock()).RunAsync(default);
        Assert.Equal(new[] { "Undeliverable", "Delivered" }, repository.Results.Select(r => r.Disposition));
        Assert.Single(telegram.Sent);
        Assert.Empty(repository.Retries);
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

    [Fact]
    public async Task FatalRepositoryFailureEscapesLambdaForAsyncRetry()
    {
        var repository = new MemoryNotifications(Work()) { FailQuery = true };
        var function = new NotificationFunction(new NotificationProcessor(repository, new NotificationTelegram()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => function.FunctionHandler(default, ContextProxy.Create()));
    }

    [Fact]
    public async Task DynamoQueueQueriesOnlyDueJobsInCurrentComplex()
    {
        using var db = new RecordingDynamo();
        db.QueryResult = new() { Items = [Stored(Work())] };
        var result = await new DynamoNotificationRepository(db, "data", "complex").ListDueAsync(Now, default);
        Assert.Single(result);
        Assert.True(db.Query!.ConsistentRead);
        Assert.Equal("NOTIFICATIONS#complex", db.Query.ExpressionAttributeValues[":pk"].S);
        Assert.Equal(ResidentNotification.DueKey(Now, "~"), db.Query.ExpressionAttributeValues[":end"].S);
        Assert.Equal(25, db.Query.Limit);
        Assert.Null(db.Query.IndexName);
    }

    [Fact]
    public async Task DynamoBotWidePauseSkipsQuery()
    {
        using var db = new RecordingDynamo { Control = new() { ["pauseUntil"] = new() { N = Now.AddMinutes(5).ToUnixTimeSeconds().ToString() } } };
        Assert.Empty(await new DynamoNotificationRepository(db, "data", "complex").ListDueAsync(Now, default));
        Assert.Null(db.Query);
    }

    [Fact]
    public async Task LeaseRequiresExistingUnclaimedJobAndExpiresAfterWorkerTimeout()
    {
        using var db = new RecordingDynamo();
        var repository = new DynamoNotificationRepository(db, "data", "complex");
        Assert.True(await repository.ClaimAsync(Work(), "lease", Now, default));
        Assert.Contains("attribute_exists(pk)", db.Update!.ConditionExpression);
        Assert.Contains("leaseExpiresAt <= :now", db.Update.ConditionExpression);
        Assert.Equal(Now.AddSeconds(90).ToUnixTimeSeconds().ToString(), db.Update.ExpressionAttributeValues[":until"].N);
        db.Leased = true;
        Assert.False(await repository.ClaimAsync(Work(), "another-lease", Now, default));
    }

    [Fact]
    public async Task WrongComplexCannotBeClaimed()
    {
        using var db = new RecordingDynamo();
        var item = Work() with { Notification = Work().Notification with { ComplexId = "other" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DynamoNotificationRepository(db, "data", "complex").ClaimAsync(item, "lease", Now, default));
        Assert.Null(db.Update);
    }

    [Fact]
    public async Task SuccessfulDeliveryAtomicallyRemovesPendingAndStoresReceiptWithTtl()
    {
        using var db = new RecordingDynamo();
        await new DynamoNotificationRepository(db, "data", "complex").CompleteAsync(Work(), "lease", "Delivered", Now, default);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(2, writes.Count);
        Assert.Equal("lease", Assert.Single(writes, w => w.Delete is not null).Delete.ExpressionAttributeValues[":lease"].S);
        var receipt = Assert.Single(writes, w => w.Put is not null).Put.Item;
        Assert.Equal("RESULT#7-v2", receipt["sk"].S);
        Assert.Equal("Delivered", receipt["disposition"].S);
        Assert.Equal(Now.AddDays(30).ToUnixTimeSeconds().ToString(), receipt["expiresAt"].N);
        Assert.False(receipt.ContainsKey("payload"));
    }

    [Fact]
    public async Task RetryMovesDueKeyAtomicallyWithoutTtlOrLeaseAndIncrementsAttempts()
    {
        using var db = new RecordingDynamo();
        await new DynamoNotificationRepository(db, "data", "complex").RetryAsync(Work(attempts: 2), "lease", Now.AddMinutes(4), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Equal(2, writes.Count);
        var pending = Assert.Single(writes, w => w.Put is not null).Put.Item;
        Assert.Equal(ResidentNotification.DueKey(Now.AddMinutes(4), "7-v2"), pending["sk"].S);
        Assert.Equal("3", pending["attempts"].N);
        Assert.False(pending.ContainsKey("expiresAt"));
        Assert.False(pending.ContainsKey("leaseToken"));
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

    private static Dictionary<string, AttributeValue> Stored(NotificationWorkItem item) => new()
    {
        ["sk"] = new(item.SortKey),
        ["payload"] = new(JsonSerializer.Serialize(item.Notification)),
        ["attempts"] = new() { N = item.Attempts.ToString() }
    };
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NotificationTelegram : ITelegramClient
    {
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
        public bool FailQuery;
        public bool FailComplete;
        public Task<IReadOnlyList<NotificationWorkItem>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
        {
            if (FailQuery) throw new InvalidOperationException("DynamoDB unavailable");
            return Task.FromResult<IReadOnlyList<NotificationWorkItem>>(PausedUntil > now ? [] : Items.ToArray());
        }
        public Task<bool> ClaimAsync(NotificationWorkItem item, string lease, DateTimeOffset now, CancellationToken ct) => Task.FromResult(CanClaim);
        public Task CompleteAsync(NotificationWorkItem item, string lease, string disposition, DateTimeOffset now, CancellationToken ct)
        { if (FailComplete) throw new InvalidOperationException("DynamoDB unavailable"); Items.Remove(item); Results.Add((item, disposition)); return Task.CompletedTask; }
        public Task RetryAsync(NotificationWorkItem item, string lease, DateTimeOffset dueAt, CancellationToken ct)
        { Retries.Add((item, dueAt)); return Task.CompletedTask; }
        public Task PauseAsync(DateTimeOffset until, CancellationToken ct) { PausedUntil = until; return Task.CompletedTask; }
    }
    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("sensitive-response") });
    }
    private sealed class RecordingDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public QueryRequest? Query;
        public QueryResponse QueryResult = new() { Items = [] };
        public UpdateItemRequest? Update;
        public TransactWriteItemsRequest? Transaction;
        public Dictionary<string, AttributeValue> Control = [];
        public bool Conflict;
        public bool Leased;
        public int TransactionCalls;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default) => Task.FromResult(new GetItemResponse { Item = Control });
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        { Query = request; return Task.FromResult(QueryResult); }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken ct = default)
        { Update = request; if (Leased) throw new ConditionalCheckFailedException("Leased"); return Task.FromResult(new UpdateItemResponse()); }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        { TransactionCalls++; Transaction = request; if (Conflict) throw new TransactionCanceledException("Conflict"); return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
