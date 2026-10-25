using System.Net;
using System.Text.Json;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Telegram;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class CommandMenuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatMenusRespectResidentStatusInBothLanguagesAndCacheOnlySuccessfulUpdates(bool registered)
    {
        using var capture = new Capture();
        using var http = new HttpClient(capture);
        var client = new TelegramClient(http, new FakeSecret("SECRET"));
        await client.SetCommandMenuAsync(99, registered, default);
        await client.SetCommandMenuAsync(99, registered, default);
        Assert.Equal(2, capture.Bodies.Count);
        var languages = new List<string>();
        foreach (var body in capture.Bodies)
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.Equal("chat", root.GetProperty("scope").GetProperty("type").GetString());
            Assert.Equal(99, root.GetProperty("scope").GetProperty("chat_id").GetInt64());
            languages.Add(root.GetProperty("language_code").GetString()!);
            var commands = root.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("command").GetString()).ToArray();
            Assert.DoesNotContain("categories", commands);
            Assert.DoesNotContain("register", commands);
            Assert.Equal(!registered, commands.Contains("start"));
            Assert.Contains("new", commands);
            Assert.Contains("request", commands);
            Assert.Equal(registered, commands.Contains("edit_profile"));
            Assert.Equal(registered, commands.Contains("delete_profile"));
        }
        Assert.Equal(new[] { "", "uk" }, languages);
        await client.SetCommandMenuAsync(99, !registered, default);
        Assert.Equal(4, capture.Bodies.Count);
        await client.SetCommandMenuAsync(99, !registered, default, force: true);
        Assert.Equal(6, capture.Bodies.Count);
        Assert.All(capture.Paths, path => Assert.EndsWith("/setMyCommands", path));
    }

    [Fact]
    public async Task PartiallyFailedMenuUpdateIsRetriedAndRespectsRateLimit()
    {
        using var capture = new Capture { RateLimitAt = 2 };
        using var http = new HttpClient(capture);
        var client = new TelegramClient(http, new FakeSecret("SECRET"));
        var error = await Assert.ThrowsAsync<TelegramRateLimitException>(() => client.SetCommandMenuAsync(99, true, default));
        Assert.Equal(37, error.RetryAfterSeconds);
        await client.SetCommandMenuAsync(99, true, default);
        Assert.Equal(4, capture.Bodies.Count);
    }

    [Fact]
    public async Task MenuTransportErrorDoesNotExposeBotToken()
    {
        using var capture = new Capture { TransportFailure = true };
        using var http = new HttpClient(capture);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new TelegramClient(http, new FakeSecret("SECRET"))
            .SetCommandMenuAsync(99, true, default));
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingSynchronizesMenuUsingCurrentMembershipBeforeSendingReply(bool registered)
    {
        var repository = new MemoryRepository();
        if (!registered) repository.Residents.Clear();
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, new CommandHandler(repository, "OsbbBot", "building"), telegram, "building");
        var command = new CommandEnvelope(1, 42, 42, "/help");
        await processor.ProcessAsync(command, default);
        await processor.ProcessAsync(command, default);
        Assert.Equal((42L, registered), Assert.Single(telegram.Menus));
        Assert.Single(telegram.Messages);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> Paths { get; } = [];
        public int RateLimitAt;
        public bool TransportFailure;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (TransportFailure) throw new HttpRequestException("https://api.telegram.org/botSECRET/setMyCommands");
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            Paths.Add(request.RequestUri!.AbsolutePath);
            return new(Bodies.Count == RateLimitAt ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                Content = new StringContent(Bodies.Count == RateLimitAt
                    ? "{\"ok\":false,\"parameters\":{\"retry_after\":37}}" : "{\"ok\":true,\"result\":true}")
            };
        }
    }
}
