using System.Net.Http.Json;
using System.Text.Json;

namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramClient(HttpClient httpClient, ISecretProvider tokenProvider) : ITelegramClient
{
    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 1 or > 4096)
            throw new InvalidOperationException("Telegram message length is outside the supported range.");
        var token = await tokenProvider.GetAsync(cancellationToken);
        // No parse_mode: all resident content is plain text, never Telegram HTML.
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage", new
                {
                    chat_id = chatId,
                    text,
                    link_preview_options = new { is_disabled = true }
                }, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                using var error = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var seconds = error.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var retryAfter) && retryAfter.TryGetInt32(out var value)
                    ? value : 180;
                throw new TelegramRateLimitException(seconds);
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Telegram delivery failed (HTTP {(int)response.StatusCode}).");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Telegram rejected the message.");
        }
        catch (HttpRequestException)
        {
            // Do not propagate an exception containing a URL with the bot token.
            throw new InvalidOperationException("Telegram transport failed.");
        }
    }
}
