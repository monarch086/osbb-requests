using System.Net;
using System.Text.Json;
using OsbbRequests.CommandListener;
using OsbbRequests.CommandProcessor.Commands;
using OsbbRequests.CommandProcessor.Telegram;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class CommandListenerTests
{
    [Fact]
    public async Task DispatchesPrivateUpdatesAndAcknowledgesIgnoredOnes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var handler = new PollingStub(
            """{"ok":true,"result":[{"update_id":10,"message":{"from":{"id":42},"chat":{"id":42,"type":"private"},"text":"/start"}},{"update_id":11}]}""",
            """{"ok":true,"result":[]}""");
        using var http = new HttpClient(handler);
        var repository = new MemoryRepository();
        var telegram = new FakeTelegram();
        var processor = new TelegramCommandProcessor(repository, new CommandHandler(repository, "OsbbBot", "building"), telegram);
        var listener = new CommandPollingLoop(new TelegramClient(http, new FakeSecret("token")), processor.ProcessAsync, "OsbbBot");

        await listener.RunAsync(_ => { }, cancellation.Token);

        Assert.Equal([0L, 12L, 0L], handler.Offsets);
        Assert.Single(telegram.Messages);
    }

    [Fact]
    public async Task RetriesFromTheFailedUpdateBeforeAcknowledgingLaterOnes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        const string failed = """{"update_id":11,"message":{"from":{"id":42},"chat":{"id":42,"type":"private"},"text":"друга"}}""";
        const string later = """{"update_id":12,"message":{"from":{"id":42},"chat":{"id":42,"type":"private"},"text":"третя"}}""";
        using var handler = new PollingStub(
            """{"ok":true,"result":[{"update_id":10,"message":{"from":{"id":42},"chat":{"id":42,"type":"private"},"text":"перша"}},""" + failed + "," + later + "]}",
            """{"ok":true,"result":[""" + failed + "," + later + "]}");
        using var http = new HttpClient(handler);
        var attempts = new List<long>();
        var listener = new CommandPollingLoop(new TelegramClient(http, new FakeSecret("token")), (command, _) =>
        {
            attempts.Add(command.UpdateId);
            return attempts.Count == 2 ? Task.FromException(new InvalidOperationException("Failure")) : Task.CompletedTask;
        }, "OsbbBot");

        await listener.RunAsync(_ => { }, cancellation.Token);

        Assert.Equal([0L, 11L, 13L], handler.Offsets);
        Assert.Equal([10L, 11L, 11L, 12L], attempts);
    }

    [Fact]
    public async Task StopsOnWebhookConflictWithoutMakingChangesToWebhook()
    {
        using var http = new HttpClient(new PollingStub("""{"ok":false,"error_code":409}""") { StatusCode = HttpStatusCode.Conflict });
        var listener = new CommandPollingLoop(new TelegramClient(http, new FakeSecret("token")),
            (_, _) => throw new InvalidOperationException("Unexpected command"), "OsbbBot");

        var error = await Assert.ThrowsAsync<TelegramApiException>(() => listener.RunAsync(_ => { }, CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal(TelegramOperation.GetUpdates, error.Operation);
    }

    private sealed class PollingStub(params string[] responses) : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public List<long> Offsets { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/getUpdates", request.RequestUri!.AbsolutePath);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(payload.RootElement.GetProperty("timeout").GetInt32() > 0);
            Assert.Equal("message", payload.RootElement.GetProperty("allowed_updates")[0].GetString());
            Offsets.Add(payload.RootElement.GetProperty("offset").GetInt64());
            if (Offsets.Count > responses.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Expected cancellation");
            }
            return new HttpResponseMessage(StatusCode) { Content = new StringContent(responses[Offsets.Count - 1]) };
        }
    }
}
