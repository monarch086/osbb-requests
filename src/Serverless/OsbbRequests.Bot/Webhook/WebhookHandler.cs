using OsbbRequests.Shared.Telegram;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Telegram;

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

        if (TelegramCommandMapper.FromUpdate(update, botUsername) is { } command)
            await queue.EnqueueAsync(command, cancellationToken);
        return Response(200);
    }

    public static APIGatewayHttpApiV2ProxyResponse Response(int statusCode) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
        Body = statusCode == 200 ? "{\"ok\":true}" : "{\"ok\":false}"
    };
}
