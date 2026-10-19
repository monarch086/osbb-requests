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
    [Fact]
    public async Task TransactionIncludesRequestReplyAndHistoryWithConditionalWrites()
    {
        using var client = new CapturingDynamo();
        var repository = new DynamoCommandRepository(client, "data");
        var command = new CommandEnvelope(100, 42, 42, "/new");
        var request = new MaintenanceRequest(100, 42, "building", "інше", "двір", "Зламано освітлення", DateTimeOffset.UtcNow);
        await repository.CommitAsync(command, new CommandOutcome("Створено", request), CancellationToken.None);
        var transaction = Assert.IsType<TransactWriteItemsRequest>(client.Transaction);
        Assert.Equal(3, transaction.TransactItems.Count);
        Assert.All(transaction.TransactItems, item => Assert.Equal("attribute_not_exists(pk)", item.Put.ConditionExpression));
        var receipt = Assert.Single(transaction.TransactItems, item => item.Put.Item["pk"].S == "UPDATE#100").Put.Item;
        Assert.True(long.Parse(receipt["expiresAt"].N) > DateTimeOffset.UtcNow.AddDays(29).ToUnixTimeSeconds());
        Assert.Equal("USER#42", transaction.TransactItems[1].Put.Item["pk"].S);
        Assert.Equal("REQUEST#100", transaction.TransactItems[2].Put.Item["pk"].S);
        Assert.All(transaction.TransactItems.Skip(1), item => Assert.False(item.Put.Item.ContainsKey("expiresAt")));
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
            Task.FromResult(new GetItemResponse { Item = Item });

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Query = request;
            return Task.FromResult(new QueryResponse { Items = [] });
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
