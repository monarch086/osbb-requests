using System.Text.Json.Serialization;

namespace OsbbRequests.Bot.Models;

public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long? UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message);

public sealed record TelegramMessage(
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("chat")] TelegramChat? Chat,
    [property: JsonPropertyName("text")] string? Text);

public sealed record TelegramUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("is_bot")] bool IsBot);

public sealed record TelegramChat(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string? Type);

// Only these fields cross into the command queue; full Telegram payloads are not retained.
public sealed record CommandEnvelope(long UpdateId, long UserId, long ChatId, string Text);
