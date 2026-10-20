using System.Net.Http.Json;
using System.Text.Json;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Telegram;

public sealed class TelegramClient(HttpClient httpClient, ISecretProvider tokenProvider) : ITelegramClient
{
    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
    {
        if (text.Length is < 1 or > 4096)
            throw new InvalidOperationException("Telegram message length is outside the supported range.");
        var token = await tokenProvider.GetAsync(cancellationToken);
        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["link_preview_options"] = new { is_disabled = true }
        };
        if (keyboard == ReplyKeyboard.RequestContact)
            payload["reply_markup"] = new
            {
                keyboard = new[] { new[] { new { text = "Поділитися номером телефону", request_contact = true } } },
                resize_keyboard = true,
                one_time_keyboard = true
            };
        else if (keyboard is ReplyKeyboard.RequestScope or ReplyKeyboard.RequestFloor or ReplyKeyboard.RequestCategory)
        {
            var rows = keyboard switch
            {
                ReplyKeyboard.RequestScope => ComplexLayout.BuildingNumbers.Select(number => "Будинок " + number).Chunk(2).Append(new[] { "Весь комплекс" }).ToArray(),
                ReplyKeyboard.RequestFloor => [["Без поверху"]],
                _ => RequestCreationService.Categories.Split(", ").Select(category => new[] { category }).ToArray()
            };
            payload["reply_markup"] = new { keyboard = rows.Select(row => row.Select(text => new { text }).ToArray()).ToArray(), resize_keyboard = true, one_time_keyboard = true };
        }
        else if (keyboard == ReplyKeyboard.Remove)
            payload["reply_markup"] = new { remove_keyboard = true };
        // No parse_mode: all resident content is plain text, never Telegram HTML.
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage", payload, cancellationToken);
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
