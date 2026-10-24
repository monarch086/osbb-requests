using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Runtime;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Bot.Webhook;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class InfrastructureTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task LookupStateAndReplyCommitAtomicallyWithVersionCheck(int? expected)
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var state = new RequestLookup(42, "building", expected is null, (expected ?? 0) + 1);
        await repository.CommitAsync(new(100, 42, 42, "/request"),
            new("Введіть номер заявки", Lookup: new(state, expected)), default);
        var writes = client.Transaction!.TransactItems;
        Assert.Equal(2, writes.Count);
        var stored = Assert.Single(writes, w => w.Put.Item["sk"].S == "REQUEST_LOOKUP").Put;
        Assert.Equal("USER#42", stored.Item["pk"].S);
        Assert.Equal(state, JsonSerializer.Deserialize<RequestLookup>(stored.Item["payload"].S));
        Assert.False(stored.Item.ContainsKey("expiresAt"));
        if (expected is null) Assert.Equal("attribute_not_exists(pk)", stored.ConditionExpression);
        else Assert.Equal("1", stored.ExpressionAttributeValues[":expected"].N);
        client.Item = stored.Item;
        Assert.Equal(state, await repository.GetRequestLookupAsync(42, default));
    }

    [Theory]
    [InlineData("Public", "New", "building", true)]
    [InlineData("Public", "InProgress", "building", true)]
    [InlineData("Private", "New", "building", false)]
    [InlineData("Public", "Completed", "building", false)]
    [InlineData("Public", "AwaitingClarification", "building", false)]
    [InlineData("Public", "Rejected", "building", false)]
    [InlineData("Public", "New", "other", false)]
    public async Task PublicQueryRechecksCurrentItemInsteadOfTrustingStaleIndex(string visibility, string status, string complex, bool visible)
    {
        using var client = new CapturingDynamo();
        var key = "2026-10-20T10:00:00.0000000+00:00#7";
        client.QueryResult = new()
        {
            Items = [new() { ["pk"] = new("USER#99"), ["sk"] = new("REQUEST#00000000000000000007") }],
            LastEvaluatedKey = new() { ["requestCreatedAt"] = new(key), ["pk"] = new("USER#99") }
        };
        var request = new MaintenanceRequest(7, 99, complex, "інше", "двір", "Приватний опис", DateTimeOffset.UtcNow,
            Enum.Parse<RequestStatus>(status), Enum.Parse<RequestVisibility>(visibility), Resolution: "Приватна відповідь");
        client.Item = new() { ["payload"] = new(JsonSerializer.Serialize(request)) };
        var page = await new DynamoCommandRepository(client, "data").ListPublicRequestsAsync("building", key, default);
        Assert.Equal(visible ? 1 : 0, page.Items.Count);
        Assert.Equal(key, page.NextBefore);
        Assert.Equal("BuildingRequestsIndex", client.Query!.IndexName);
        Assert.Equal("requestBuildingId = :complex AND requestCreatedAt < :before", client.Query.KeyConditionExpression);
        Assert.Equal("building", client.Query.ExpressionAttributeValues[":complex"].S);
        Assert.Equal("requestStatus IN (:new, :progress)", client.Query.FilterExpression);
        Assert.Equal("pk, sk", client.Query.ProjectionExpression);
        Assert.Equal(5, client.Query.Limit);
        Assert.False(client.Query.ScanIndexForward);
        Assert.True(client.Get!.ConsistentRead);
        Assert.Equal("USER#99", client.Get.Key["pk"].S);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("OwnerId", json);
        if (visible) Assert.Equal(request.Description, Assert.Single(page.Items).Description);
        else Assert.DoesNotContain("Description", json);
        Assert.DoesNotContain("Resolution", json);
    }

    [Fact]
    public async Task DeletedIndexEntryIsSkippedAndCursorIsPreserved()
    {
        using var client = new CapturingDynamo();
        client.QueryResult = new()
        {
            Items = [new() { ["pk"] = new("USER#99"), ["sk"] = new("REQUEST#7") }],
            LastEvaluatedKey = new() { ["requestCreatedAt"] = new("cursor") }
        };
        var page = await new DynamoCommandRepository(client, "data").ListPublicRequestsAsync("building", null, default);
        Assert.Empty(page.Items);
        Assert.Equal("cursor", page.NextBefore);
    }

    [Fact]
    public async Task DeletionCommitsReceiptAuditStateAndConditionalDeleteAtomically()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var lookup = new RequestLookup(42, "building", false, 3);
        await repository.CommitAsync(new(100, 42, 42, "Так"), new("Видалено", Lookup: new(lookup, 2),
            Deletion: new(7, 42, "building", 4)), default);
        var writes = client.Transaction!.TransactItems;
        Assert.Equal(5, writes.Count);
        var delete = Assert.Single(writes, w => w.Delete is not null).Delete;
        Assert.Equal("USER#42", delete.Key["pk"].S);
        Assert.Equal("REQUEST#00000000000000000007", delete.Key["sk"].S);
        Assert.Contains("#version = :expected", delete.ConditionExpression);
        Assert.Contains("requestStatus = :new", delete.ConditionExpression);
        Assert.Contains("requestBuildingId = :complex", delete.ConditionExpression);
        Assert.Equal("4", delete.ExpressionAttributeValues[":expected"].N);
        Assert.Equal("New", delete.ExpressionAttributeValues[":new"].S);
        Assert.Equal("building", delete.ExpressionAttributeValues[":complex"].S);
        var access = Assert.Single(writes, w => w.ConditionCheck is not null).ConditionCheck;
        Assert.Equal("MEMBERSHIP", access.Key["sk"].S);
        Assert.Contains("#active = :active", access.ConditionExpression);
        Assert.Equal("active", access.ExpressionAttributeNames["#active"]);
        Assert.True(access.ExpressionAttributeValues[":active"].BOOL);
        var audit = Assert.Single(writes, w => w.Put?.Item["sk"].S == "EVENT#DELETED").Put.Item;
        Assert.Equal("REQUEST#7", audit["pk"].S);
        Assert.Equal("Deleted", audit["action"].S);
        Assert.False(audit.ContainsKey("expiresAt"));
        Assert.Contains(writes, w => w.Put?.Item["sk"].S == "REPLY");
        Assert.Contains(writes, w => w.Put?.Item["sk"].S == "REQUEST_LOOKUP");
    }

    [Fact]
    public async Task DeletionWithWrongOwnerIsRejectedBeforeTransaction()
    {
        using var client = new CapturingDynamo();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DynamoCommandRepository(client, "data")
            .CommitAsync(new(100, 42, 42, "Так"), new("Видалено", Deletion: new(7, 99, "building", 1)), default));
        Assert.Null(client.Transaction);
    }

    [Fact]
    public async Task DeletionSnapshotUsesConsistentOwnerReadAndStoredVersion()
    {
        using var client = new CapturingDynamo();
        var request = new MaintenanceRequest(7, 42, "building", "інше", "двір", "Освітлення", DateTimeOffset.UtcNow);
        client.Item = new() { ["payload"] = new(JsonSerializer.Serialize(request)), ["version"] = new() { N = "3" } };
        var result = await new DynamoCommandRepository(client, "data").GetDeletableRequestAsync(42, 7, default);
        Assert.Equal(new DeletableRequest(7, 42, "building", RequestStatus.New, 3), result);
        Assert.True(client.Get!.ConsistentRead);
        Assert.Equal("USER#42", client.Get.Key["pk"].S);
        Assert.Equal("REQUEST#00000000000000000007", client.Get.Key["sk"].S);
    }

    [Fact]
    public async Task PendingRegistrationCommitsProfileReviewIndexAndReplyAtomically()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var registration = new ResidentRegistration(new string('a', 32), 99, "building", RegistrationStage.Pending,
            4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "+380671234567", "Олена", "Ковальчук");
        var result = await repository.CommitAsync(new CommandEnvelope(100, 99, 99, "Ковальчук"),
            new CommandOutcome("Очікуйте перевірки", Registration: new RegistrationChange(registration, 3),
                Keyboard: ReplyKeyboard.Remove), CancellationToken.None);
        Assert.Equal(ReplyKeyboard.Remove, result.Keyboard);
        var writes = Assert.IsType<TransactWriteItemsRequest>(client.Transaction).TransactItems;
        Assert.Equal(5, writes.Count);
        var profile = Assert.Single(writes, item => item.Put.Item["sk"].S == "REGISTRATION").Put;
        Assert.Equal("#version = :expected", profile.ConditionExpression);
        Assert.Equal("3", profile.ExpressionAttributeValues[":expected"].N);
        Assert.Contains(writes, item => item.Put.Item["pk"].S == "BUILDING#building");
        Assert.Contains(writes, item => item.Put.Item["sk"].S == "META");
        Assert.DoesNotContain(writes, item => item.Put.Item["sk"].S == "MEMBERSHIP");
        var broadcast = Assert.Single(writes, item => item.Put.Item["pk"].S == "NOTIFICATIONS#building").Put.Item;
        var notification = JsonSerializer.Deserialize<ResidentNotification>(broadcast["payload"].S)!;
        Assert.True(notification.IsBroadcast);
        Assert.Equal(AdministratorEvent.NewResident, notification.AdministratorEvent);
        Assert.False(broadcast.ContainsKey("expiresAt"));
    }

    [Fact]
    public async Task DraftIsNotPublishedForReviewOrGrantedMembership()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var registration = new ResidentRegistration(new string('a', 32), 99, "building", RegistrationStage.AwaitingPhone,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await repository.CommitAsync(new CommandEnvelope(100, 99, 99, "/register"),
            new CommandOutcome("Номер телефону", Registration: new RegistrationChange(registration, null),
                Keyboard: ReplyKeyboard.RequestContact), CancellationToken.None);
        var writes = Assert.IsType<TransactWriteItemsRequest>(client.Transaction).TransactItems;
        Assert.Equal(2, writes.Count);
        Assert.DoesNotContain(writes, item => item.Put.Item["pk"].S.StartsWith("BUILDING#"));
        Assert.DoesNotContain(writes, item => item.Put.Item["sk"].S == "MEMBERSHIP");
        Assert.All(writes, item => Assert.Equal("attribute_not_exists(pk)", item.Put.ConditionExpression));
    }

    [Fact]
    public async Task TransactionIncludesRequestReplyAndHistoryWithConditionalWrites()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var command = new CommandEnvelope(100, 42, 42, "/new");
        var request = new MaintenanceRequest(100, 42, "building", "інше", "двір", "Зламано освітлення", DateTimeOffset.UtcNow, Scope: RequestScope.Complex);
        await repository.CommitAsync(command, new CommandOutcome("Створено", request), CancellationToken.None);
        var transaction = Assert.IsType<TransactWriteItemsRequest>(client.Transaction);
        Assert.Equal(4, transaction.TransactItems.Count);
        Assert.All(transaction.TransactItems, item => Assert.Equal("attribute_not_exists(pk)", item.Put.ConditionExpression));
        var receipt = Assert.Single(transaction.TransactItems, item => item.Put.Item["pk"].S == "UPDATE#100").Put.Item;
        Assert.True(long.Parse(receipt["expiresAt"].N) > DateTimeOffset.UtcNow.AddDays(29).ToUnixTimeSeconds());
        Assert.Equal("USER#42", transaction.TransactItems[1].Put.Item["pk"].S);
        Assert.Equal("REQUEST#100", transaction.TransactItems[2].Put.Item["pk"].S);
        var broadcast = Assert.Single(transaction.TransactItems, item => item.Put.Item["pk"].S == "NOTIFICATIONS#building").Put.Item;
        Assert.Equal(AdministratorEvent.NewRequest, JsonSerializer.Deserialize<ResidentNotification>(broadcast["payload"].S)!.AdministratorEvent);
        Assert.All(transaction.TransactItems.Skip(1), item => Assert.False(item.Put.Item.ContainsKey("expiresAt")));
    }

    [Fact]
    public async Task WizardFinalizationCommitsDraftVersionRequestTargetHistoryAndReplyAtomically()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var request = new MaintenanceRequest(100, 42, "complex", "інше", "ліфт", "Ліфт не працює", DateTimeOffset.UtcNow,
            Scope: RequestScope.Building, BuildingNumber: "4В", Floor: 25);
        var draft = new RequestDraft(42, "complex", RequestDraftStage.Completed, 7);
        await repository.CommitAsync(new(100, 42, 42, "Ліфт не працює"), new("Створено", request, Draft: new(draft, 6)), default);
        var writes = client.Transaction!.TransactItems;
        Assert.Equal(5, writes.Count);
        var stored = Assert.Single(writes, w => w.Put.Item["sk"].S.StartsWith("REQUEST#", StringComparison.Ordinal)).Put.Item;
        Assert.Equal("Building4В", stored["requestTarget"].S);
        using var payload = JsonDocument.Parse(stored["payload"].S);
        Assert.Equal(JsonValueKind.String, payload.RootElement.GetProperty("BuildingNumber").ValueKind);
        Assert.Equal("4В", payload.RootElement.GetProperty("BuildingNumber").GetString());
        Assert.Equal("25", stored["requestFloor"].N);
        var state = Assert.Single(writes, w => w.Put.Item["sk"].S == "REQUEST_DRAFT").Put;
        Assert.Equal("6", state.ExpressionAttributeValues[":expected"].N);
        Assert.Contains(":expected", state.ConditionExpression);
        Assert.False(stored.ContainsKey("expiresAt"));
    }

    [Fact]
    public async Task WizardDraftPersistsAdministrativeBuildingNumberAsString()
    {
        using var client = new CapturingDynamo();
        var draft = new RequestDraft(42, "complex", RequestDraftStage.Floor, 2, RequestScope.Building, "4Г");
        await new DynamoCommandRepository(client, "data").CommitAsync(new(2, 42, 42, "Будинок 4Г"),
            new("Поверх", Draft: new(draft, 1)), default);
        var stored = Assert.Single(client.Transaction!.TransactItems, w => w.Put.Item["sk"].S == "REQUEST_DRAFT").Put.Item;
        using var payload = JsonDocument.Parse(stored["payload"].S);
        Assert.Equal("4Г", payload.RootElement.GetProperty("BuildingNumber").GetString());
    }

    [Fact]
    public async Task ListsUsingOwnerKeyAndCursorWithoutScan()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        await repository.ListRequestsAsync(42, 100, CancellationToken.None);
        var query = Assert.IsType<QueryRequest>(client.Query);
        Assert.True(query.ConsistentRead);
        Assert.False(query.ScanIndexForward);
        Assert.Equal("USER#42", query.ExpressionAttributeValues[":pk"].S);
        Assert.Equal("USER#42", query.ExclusiveStartKey["pk"].S);
        Assert.Equal("REQUEST#00000000000000000100", query.ExclusiveStartKey["sk"].S);
    }

    [Fact]
    public async Task ConcurrentCommitUsesWinningStoredReply()
    {
        using var client = new CapturingDynamo { Conflict = true };
        var repository = new DynamoCommandRepository(client, "data");
        var existing = new StoredReply(100, 42, 42, "Перша відповідь", false);
        client.Item = new Dictionary<string, AttributeValue>
        {
            ["payload"] = new(JsonSerializer.Serialize(existing)),
            ["delivered"] = new() { BOOL = true }
        };
        var result = await repository.CommitAsync(new CommandEnvelope(100, 42, 42, "/help"),
            new CommandOutcome("Інша відповідь"), CancellationToken.None);
        Assert.Equal("Перша відповідь", result.Text);
        Assert.True(result.Delivered);
    }

    [Fact]
    public async Task TransactionFailureWithoutWinningReceiptIsNotSwallowed()
    {
        using var client = new CapturingDynamo { Conflict = true };
        var repository = new DynamoCommandRepository(client, "data");
        await Assert.ThrowsAsync<TransactionCanceledException>(() => repository.CommitAsync(
            new CommandEnvelope(100, 42, 42, "/help"), new CommandOutcome("Допомога"), CancellationToken.None));
    }

    [Theory]
    [InlineData(false, "building")]
    [InlineData(true, "other-building")]
    public async Task MembershipMustBeActiveAndMatchBuilding(bool active, string building)
    {
        using var client = new CapturingDynamo
        {
            Item = new Dictionary<string, AttributeValue>
            {
                ["buildingId"] = new(building),
                ["active"] = new() { BOOL = active }
            }
        };
        Assert.False(await new DynamoCommandRepository(client, "data").IsResidentAsync(42, "building", CancellationToken.None));
    }

    [Fact]
    public async Task TelegramUsesPlainTextAndDisablesLinkPreviews()
    {
        string? sent = null;
        using var http = new HttpClient(new HttpStub(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }));
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(42, "<b>Місце</b>", CancellationToken.None);
        using var body = JsonDocument.Parse(Assert.IsType<string>(sent));
        Assert.False(body.RootElement.TryGetProperty("parse_mode", out _));
        Assert.Equal("<b>Місце</b>", body.RootElement.GetProperty("text").GetString());
        Assert.True(body.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Theory]
    [InlineData(ReplyKeyboard.RequestScope, "Будинок 4Д")]
    [InlineData(ReplyKeyboard.RequestFloor, "Без поверху")]
    [InlineData(ReplyKeyboard.RequestFloor, "Парадне")]
    [InlineData(ReplyKeyboard.RequestCategory, "сантехніка")]
    public async Task WizardChoicesAreDeliveredAsReplyKeyboardButtons(ReplyKeyboard keyboard, string choice)
    {
        string? sent = null;
        using var http = new HttpClient(new HttpStub(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }));
        await new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(42, "Оберіть", default, keyboard);
        using var body = JsonDocument.Parse(Assert.IsType<string>(sent));
        var markup = body.RootElement.GetProperty("reply_markup");
        var labels = markup.GetProperty("keyboard").EnumerateArray().SelectMany(row => row.EnumerateArray())
            .Select(button => button.GetProperty("text").GetString()).ToList();
        Assert.Contains(choice, labels);
        Assert.True(markup.GetProperty("resize_keyboard").GetBoolean());
        Assert.True(markup.GetProperty("one_time_keyboard").GetBoolean());
    }

    [Fact]
    public async Task TelegramRateLimitRetainsRetryAfter()
    {
        using var http = new HttpClient(new HttpStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"ok\":false,\"parameters\":{\"retry_after\":400}}")
        })));
        var error = await Assert.ThrowsAsync<TelegramRateLimitException>(() =>
            new TelegramClient(http, new FakeSecret("token")).SendMessageAsync(42, "Текст", CancellationToken.None));
        Assert.Equal(400, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task TransportExceptionDoesNotExposeBotToken()
    {
        using var http = new HttpClient(new HttpStub(_ => throw new HttpRequestException("https://api.telegram.org/botSECRET/sendMessage")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TelegramClient(http, new FakeSecret("SECRET")).SendMessageAsync(42, "Текст", CancellationToken.None));
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task FifoBatchStopsAtFirstFailureAndReturnsRemainingRecords()
    {
        var repository = new MemoryRepository { FailCommit = true };
        var processor = new CommandProcessor(repository, new CommandHandler(repository, "OsbbBot", "building"), new FakeTelegram());
        var input = new SQSEvent
        {
            Records = [Record("first", 100), Record("second", 101)]
        };
        var response = await new CommandFunction(processor).FunctionHandler(input, ContextProxy.Create());
        Assert.Equal(new[] { "first", "second" }, response.BatchItemFailures.Select(item => item.ItemIdentifier));
        Assert.Empty(repository.Replies);
    }

    [Fact]
    public async Task WebhookEntryReturns503WhenQueueFails()
    {
        var handler = new WebhookHandler(new FakeSecret("secret"), new FakeQueue { Fail = true }, "OsbbBot");
        var response = await new Function(handler).FunctionHandler(new()
        {
            Headers = new Dictionary<string, string> { ["x-telegram-bot-api-secret-token"] = "secret" },
            Body = WebhookTests.Update()
        }, ContextProxy.Create());
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task RateLimitDefersSqsMessageWithoutMarkingReplyDelivered()
    {
        var repository = new MemoryRepository();
        using var http = new HttpClient(new HttpStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"ok\":false,\"parameters\":{\"retry_after\":400}}")
        })));
        var processor = new CommandProcessor(repository, new CommandHandler(repository, "OsbbBot", "building"),
            new TelegramClient(http, new FakeSecret("token")));
        var retry = new FakeRetryScheduler();
        var response = await new CommandFunction(processor, retry).FunctionHandler(
            new SQSEvent { Records = [Record("limited", 100)] }, ContextProxy.Create());
        Assert.Single(response.BatchItemFailures);
        Assert.Equal(("receipt", 400), Assert.Single(retry.Delays));
        Assert.False(repository.Replies[100].Delivered);
    }

    [Fact]
    public async Task SuccessfulQueueMessageCanBeRedeliveredWithoutAnotherTelegramMessage()
    {
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, new CommandHandler(repository, "OsbbBot", "building"), telegram);
        var function = new CommandFunction(processor);
        var input = new SQSEvent { Records = [Record("first", 100)] };
        Assert.Empty((await function.FunctionHandler(input, ContextProxy.Create())).BatchItemFailures);
        Assert.Empty((await function.FunctionHandler(input, ContextProxy.Create())).BatchItemFailures);
        Assert.Single(telegram.Messages);
    }

    [Fact]
    public async Task OperationalNotificationFailureDoesNotEscape()
    {
        var telegram = new FakeTelegram { Fail = true };
        await new ServiceNotifier(telegram, 123).NotifyFailureAsync("failed-message");
        Assert.Empty(telegram.Messages);
    }

    private static SQSEvent.SQSMessage Record(string id, long updateId) => new()
    {
        MessageId = id,
        ReceiptHandle = "receipt",
        Body = JsonSerializer.Serialize(new CommandEnvelope(updateId, 42, 42, "/help"))
    };

    private sealed class HttpStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeRetryScheduler : IQueueRetryScheduler
    {
        public List<(string ReceiptHandle, int Seconds)> Delays { get; } = [];
        public Task DeferAsync(string receiptHandle, int retryAfterSeconds, CancellationToken cancellationToken)
        {
            Delays.Add((receiptHandle, retryAfterSeconds));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public TransactWriteItemsRequest? Transaction { get; private set; }
        public QueryRequest? Query { get; private set; }
        public GetItemRequest? Get { get; private set; }
        public QueryResponse QueryResult { get; set; } = new() { Items = [] };
        public Dictionary<string, AttributeValue> Item { get; set; } = [];
        public bool Conflict { get; set; }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            Transaction = request;
            if (Conflict)
                throw new TransactionCanceledException("Concurrent write.");
            return Task.FromResult(new TransactWriteItemsResponse());
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default) =>
            CaptureGet(request);

        private Task<GetItemResponse> CaptureGet(GetItemRequest request)
        {
            Get = request;
            return Task.FromResult(new GetItemResponse { Item = Item });
        }

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Query = request;
            return Task.FromResult(QueryResult);
        }
    }
}

public class ContextProxy : DispatchProxy
{
    private sealed class Logger : ILambdaLogger
    {
        public void Log(string message) { }
        public void LogLine(string message) { }
    }

    public static ILambdaContext Create() => Create<ILambdaContext, ContextProxy>();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_Logger" ? new Logger() : null;
}
