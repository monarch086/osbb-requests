using System.Net;
using System.Text.Json;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Telegram;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class TelegramClientTests
{
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
    public async Task TelegramReturnsSentNotificationMessageId()
    {
        using var http = new HttpClient(new HttpStub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":123}}") })));
        Assert.Equal(123, await new TelegramClient(http, new FakeSecret("token")).SendNotificationAsync(42, "Текст", default));
    }

    [Theory]
    [InlineData(ReplyKeyboard.RequestScope, "Будинок 4Д")]
    [InlineData(ReplyKeyboard.RequestScope, "Прибудинкова територія")]
    [InlineData(ReplyKeyboard.RequestFloor, "1")]
    [InlineData(ReplyKeyboard.RequestFloor, "25")]
    [InlineData(ReplyKeyboard.RequestEntrance, "1")]
    [InlineData(ReplyKeyboard.RequestEntrance, "2")]
    [InlineData(ReplyKeyboard.RequestEntrance, "Не зазначено")]
    [InlineData(ReplyKeyboard.RequestCategory, "Сантехніка МЗК")]
    [InlineData(ReplyKeyboard.RequestCategory, "Ліфти")]
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
        var error = await Assert.ThrowsAsync<TelegramApiException>(() =>
            new TelegramClient(http, new FakeSecret("SECRET")).SendMessageAsync(42, "Текст", CancellationToken.None));
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task TransportFailureRecordsSafeNetworkCodesAndAvailableHttpStatus()
    {
        var socket = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        using var http = new HttpClient(new HttpStub(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError,
            "https://api.telegram.org/botSECRET/sendMessage", socket, HttpStatusCode.BadGateway)));
        var error = await Assert.ThrowsAsync<TelegramApiException>(() =>
            new TelegramClient(http, new FakeSecret("SECRET")).SendMessageAsync(42, "Текст", default));
        var details = TelegramFailureDiagnostics.For(error);
        Assert.Contains("telegramFailure=Transport", details);
        Assert.Contains("telegramHttpStatus=502", details);
        Assert.Contains("telegramOperation=SendMessage", details);
        Assert.Contains("httpRequestError=NameResolutionError", details);
        Assert.Contains("socketError=HostNotFound", details);
        Assert.DoesNotContain("SECRET", details + error);
        Assert.DoesNotContain("https://", details + error);
    }

    [Fact]
    public async Task TelegramTimeoutIsClassifiedWhileCallerCancellationIsPreserved()
    {
        using var http = new HttpClient(new HttpStub(_ => throw new TaskCanceledException("SECRET")));
        var client = new TelegramClient(http, new FakeSecret("SECRET"));
        var error = await Assert.ThrowsAsync<TelegramApiException>(() => client.SendMessageAsync(42, "Текст", default));
        Assert.Equal(TelegramFailureCategory.Timeout, error.Category);
        Assert.DoesNotContain("SECRET", error.ToString());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendMessageAsync(42, "Текст", cancellation.Token));
    }

    [Fact]
    public async Task OperationalNotificationFailureDoesNotEscape()
    {
        using var http = new HttpClient(new HttpStub(_ => throw new HttpRequestException("URL contains SECRET")));
        var logs = new List<string>();
        await new TelegramServiceNotifier(http, new FakeSecret("SECRET"), "worker", "dev")
            .NotifyFailureAsync("process_command", new InvalidOperationException("private payload"), "failed-message", logs.Add);
        Assert.Contains("HttpRequestException", Assert.Single(logs));
        Assert.DoesNotContain("SECRET", string.Join("\n", logs));
    }

    private sealed class HttpStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
