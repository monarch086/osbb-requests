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
        else if (keyboard is ReplyKeyboard.RequestScope or ReplyKeyboard.RequestFloor or ReplyKeyboard.RequestCategory or ReplyKeyboard.RequestEntrance or ReplyKeyboard.RegistrationBuilding or ReplyKeyboard.ProfileDeleteConfirmation)
        {
            var rows = keyboard switch
            {
                ReplyKeyboard.RequestScope => ComplexLayout.BuildingNumbers.Select(number => "Будинок " + number).Chunk(2).Append(new[] { "Весь комплекс" }).ToArray(),
                ReplyKeyboard.ProfileDeleteConfirmation => [[ResidentProfileService.Confirmation], ["Скасувати"]],
                ReplyKeyboard.RegistrationBuilding => ComplexLayout.BuildingNumbers.Select(number => "Будинок " + number).Chunk(2).ToArray(),
                ReplyKeyboard.RequestEntrance => [["1", "2", "Не зазначено"]],
                ReplyKeyboard.RequestFloor => [["Парадне", "Без поверху"]],
                _ => RequestCreationService.Categories.Split(", ").Select(category => new[] { category }).ToArray()
            };
            payload["reply_markup"] = new { keyboard = rows.Select(row => row.Select(text => new { text }).ToArray()).ToArray(), resize_keyboard = true, one_time_keyboard = true };
        }
        else if (keyboard == ReplyKeyboard.Remove)
            payload["reply_markup"] = new { remove_keyboard = true };
        // No parse_mode: all resident content is plain text, never Telegram HTML.
        await CallAsync("sendMessage", payload, cancellationToken);
    }

    private readonly Dictionary<long, bool> configuredMenus = [];
    public async Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken, bool force = false)
    {
        if (chatId <= 0) throw new InvalidOperationException("Private command menu requires a resident chat.");
        lock (configuredMenus)
            if (!force && configuredMenus.TryGetValue(chatId, out var current) && current == registered) return;
        foreach (var language in new[] { "", "uk" })
            await CallAsync("setMyCommands", new
            {
                scope = new { type = "chat", chat_id = chatId },
                language_code = language,
                commands = CommandMenu.ForResident(registered).Select(item => new { command = item.Command, description = item.Description }).ToArray()
            }, cancellationToken, requireTrueResult: true);
        lock (configuredMenus)
        {
            // Bound the warm Lambda cache; a cache miss only repeats an idempotent menu update.
            if (configuredMenus.Count >= 1000) configuredMenus.Clear();
            configuredMenus[chatId] = registered;
        }
    }

    private async Task CallAsync(string method, object payload, CancellationToken cancellationToken, bool requireTrueResult = false)
    {
        var token = await tokenProvider.GetAsync(cancellationToken);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/{method}", payload, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                using var error = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var seconds = error.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var retryAfter) && retryAfter.TryGetInt32(out var value)
                    ? value : 180;
                throw new TelegramRateLimitException(seconds);
            }
            if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden)
                throw new TelegramRecipientUnavailableException((int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Telegram delivery failed (HTTP {(int)response.StatusCode}).");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!body.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Telegram rejected the operation.");
            if (requireTrueResult && (!body.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.True))
                throw new InvalidOperationException("Telegram did not confirm the menu update.");
        }
        catch (HttpRequestException)
        {
            // Do not propagate an exception containing a URL with the bot token.
            throw new InvalidOperationException("Telegram transport failed.");
        }
    }
}
