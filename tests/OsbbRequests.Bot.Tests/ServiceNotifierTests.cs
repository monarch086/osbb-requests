using System.Net;
using System.Text.Json;
using OsbbRequests.Shared.Telegram;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ServiceNotifierTests
{
    [Fact]
    public async Task SendsSafeDiagnosticMetadataToTheSameServiceChat()
    {
        string? payload = null;
        using var http = new HttpClient(new HttpStub(async (request, ct) =>
        {
            payload = await request.Content!.ReadAsStringAsync(ct);
            return Ok();
        }));
        await new TelegramServiceNotifier(http, new FakeSecret("SERVICE_SECRET"), "osbb-dev-commandProcessor", "dev")
            .NotifyFailureAsync("process_command", new InvalidOperationException("SECRET +380500000000 private resident text"), "aws-request");
        using var json = JsonDocument.Parse(payload!);
        Assert.Equal("38627946", json.RootElement.GetProperty("chat_id").GetString());
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("osbb-dev-commandProcessor", text);
        Assert.Contains("dev", text);
        Assert.Contains("process_command", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("aws-request", text);
        Assert.DoesNotContain("SECRET", payload);
        Assert.DoesNotContain("380500000000", payload);
        Assert.DoesNotContain("private resident text", payload);
        Assert.False(json.RootElement.TryGetProperty("parse_mode", out _));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresAreBoundedAndDoNotExposeResponseOrToken(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new HttpStub((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET") });
        }));
        var logs = new List<string>();
        await new TelegramServiceNotifier(http, new FakeSecret("SECRET"), "worker", "dev")
            .NotifyFailureAsync("worker", new Exception("PRIVATE"), log: logs.Add);
        Assert.Equal(1, calls);
        Assert.Contains($"statusCode={status}", Assert.Single(logs));
        Assert.DoesNotContain("SECRET", logs[0]);
        Assert.DoesNotContain("PRIVATE", logs[0]);
    }

    [Fact]
    public async Task ServiceFailureDoesNotEscapeWhenSecretCannotBeRead()
    {
        using var http = new HttpClient(new HttpStub((_, _) => throw new InvalidOperationException("HTTP should not be called")));
        var logs = new List<string>();
        await new TelegramServiceNotifier(http, new UnavailableSecret(), "worker", "dev")
            .NotifyFailureAsync("worker", new Exception(), log: logs.Add);
        Assert.Contains("InvalidOperationException", Assert.Single(logs));
        Assert.DoesNotContain("SECRET", logs[0]);
    }

    [Fact]
    public async Task TimeoutCancelsServiceDeliveryWithoutMaskingProcessingFailure()
    {
        var cancelled = false;
        using var http = new HttpClient(new HttpStub(async (_, ct) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Ok();
        }));
        await new TelegramServiceNotifier(http, new FakeSecret("SECRET"), "worker", "dev")
            .NotifyFailureAsync("worker", new Exception()).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(cancelled);
    }

    [Fact]
    public async Task RejectResponseIsLoggedAndDiagnosticFieldsAreLengthLimited()
    {
        string? text = null;
        using var http = new HttpClient(new HttpStub(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            text = json.RootElement.GetProperty("text").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":false,\"description\":\"SECRET\"}") };
        }));
        var logs = new List<string>();
        var longValue = new string('X', 10000);
        await new TelegramServiceNotifier(http, new FakeSecret("SECRET"), longValue, longValue)
            .NotifyFailureAsync(longValue, new Exception(), longValue, logs.Add);
        Assert.InRange(text!.Length, 1, 4096);
        Assert.Equal("operation=notify_service outcome=rejected", Assert.Single(logs));
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
    private sealed class HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private sealed class UnavailableSecret : ISecretProvider
    {
        public Task<string> GetAsync(CancellationToken ct) => throw new InvalidOperationException("SECRET");
    }
}
