using OsbbRequests.Shared.Telegram;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Telegram;

namespace OsbbRequests.Bot.Webhook;

public sealed class WebhookHandler(ISecretProvider secretProvider, ICommandQueue queue, string botUsername)
{
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(
        APIGatewayHttpApiV2ProxyRequest request, CancellationToken cancellationToken)
    {
        var provided = request.Headers?.FirstOrDefault(p =>
            string.Equals(p.Key, "x-telegram-bot-api-secret-token", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(provided))
            return Response(403);
        var expected = await secretProvider.GetAsync(cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(provided))))
            return Response(403);

        TelegramUpdate? update;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 100_000)
                return Response(200);
            var json = request.IsBase64Encoded
                ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body)) : request.Body;
            update = JsonSerializer.Deserialize<TelegramUpdate>(json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Response(200);
        }

        if (update?.UpdateId is not (>= 0) || update.Message is not
            {
                From: { IsBot: false, Id: > 0 } from,
                Chat: { Type: "private", Id: > 0 } chat
            } message || from.Id != chat.Id)
            return Response(200);

        var text = message.Text ?? "";
        if (text.Length > 4096 || message.Contact?.PhoneNumber?.Length > 64 ||
            string.IsNullOrWhiteSpace(text) && message.Contact is null)
            return Response(200);

        // Commands explicitly addressed to another bot are ignored.
        if (text.StartsWith('/') && text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].Contains('@') &&
            BotCommand.Parse(text, botUsername) is null)
            return Response(200);

        await queue.EnqueueAsync(new CommandEnvelope(update.UpdateId.Value, from.Id, chat.Id, text, message.Contact), cancellationToken);
        return Response(200);
    }

    public static APIGatewayHttpApiV2ProxyResponse Response(int statusCode) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
        Body = statusCode == 200 ? "{\"ok\":true}" : "{\"ok\":false}"
    };
}
