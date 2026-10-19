using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.Bot.Webhook;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class WebhookTests
{
    private static APIGatewayHttpApiV2ProxyRequest Request(string body, string? secret = "secret", bool base64 = false) => new()
    {
        Headers = secret is null ? [] : new Dictionary<string, string>
        {
            ["X-Telegram-Bot-Api-Secret-Token"] = secret
        },
        Body = body,
        IsBase64Encoded = base64
    };

    internal static string Update(string? text = "/help", string chatType = "private", long userId = 42,
        long chatId = 42, bool isBot = false) => JsonSerializer.Serialize(new
        {
            update_id = 100,
            message = new { from = new { id = userId, is_bot = isBot }, chat = new { id = chatId, type = chatType }, text }
        });

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect")]
    public async Task RejectsUnauthorizedBeforeParsingOrEnqueueing(string? secret)
    {
        var queue = new FakeQueue();
        var provider = new FakeSecret("secret");
        var response = await new WebhookHandler(provider, queue, "OsbbBot")
            .HandleAsync(Request("{malformed", secret), CancellationToken.None);
        Assert.Equal(403, response.StatusCode);
        Assert.Empty(queue.Commands);
        if (secret is null)
            Assert.Equal(0, provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptsAuthenticatedPrivateMessage(bool base64)
    {
        var queue = new FakeQueue();
        var json = Update("/START@OsbbBot");
        var response = await new WebhookHandler(new FakeSecret("secret"), queue, "OsbbBot").HandleAsync(
            Request(base64 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) : json, base64: base64), CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        var command = Assert.Single(queue.Commands);
        Assert.Equal(100, command.UpdateId);
        Assert.Equal(42, command.UserId);
        Assert.Equal("/START@OsbbBot", command.Text);
    }

    [Theory]
    [InlineData("{broken", false)]
    [InlineData("not-base64!", true)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    [InlineData("{\"update_id\":100,\"edited_message\":{}}", false)]
    public async Task AcknowledgesMalformedOrUnsupportedUpdates(string body, bool base64)
    {
        var queue = new FakeQueue();
        var response = await new WebhookHandler(new FakeSecret("secret"), queue, "OsbbBot")
            .HandleAsync(Request(body, base64: base64), CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Empty(queue.Commands);
    }

    [Fact]
    public async Task IgnoresGroupsBotsForeignCommandsAndAttachments()
    {
        var queue = new FakeQueue();
        var handler = new WebhookHandler(new FakeSecret("secret"), queue, "OsbbBot");
        foreach (var json in new[] { Update(chatType: "supergroup"), Update(isBot: true),
            Update(chatId: 99), Update("/help@OtherBot"), Update(text: null) })
        {
            var response = await handler.HandleAsync(Request(json), CancellationToken.None);
            Assert.Equal(200, response.StatusCode);
        }
        Assert.Empty(queue.Commands);
    }

    [Fact]
    public async Task DoesNotAcknowledgeWhenQueueFails()
    {
        var handler = new WebhookHandler(new FakeSecret("secret"), new FakeQueue { Fail = true }, "OsbbBot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Request(Update()), CancellationToken.None));
    }
}
