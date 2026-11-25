using System.Text.Json;

namespace OsbbRequests.Bot.Telegram;

public sealed record TelegramErrorResponse(bool? Ok, int? ErrorCode, TelegramRejectionReason Reason, int? RetryAfterSeconds)
{
    public string? SafeDescription => Reason switch
    {
        TelegramRejectionReason.BotBlocked => "Forbidden: bot was blocked by the user",
        TelegramRejectionReason.UserDeactivated => "Forbidden: user is deactivated",
        TelegramRejectionReason.ChatNotFound => "Bad Request: chat not found",
        TelegramRejectionReason.BotCannotInitiate => "Forbidden: bot can't initiate conversation with a user",
        TelegramRejectionReason.MessageTooLong => "Bad Request: message is too long",
        TelegramRejectionReason.MessageTextEmpty => "Bad Request: message text is empty",
        _ => null
    };

    public static TelegramErrorResponse Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return new(null, null, TelegramRejectionReason.Unrecognized, null);
        var ok = root.TryGetProperty("ok", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : (bool?)null;
        var reason = root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
            ? description.GetString() switch
            {
                "Forbidden: bot was blocked by the user" => TelegramRejectionReason.BotBlocked,
                "Forbidden: user is deactivated" => TelegramRejectionReason.UserDeactivated,
                "Bad Request: chat not found" => TelegramRejectionReason.ChatNotFound,
                "Forbidden: bot can't initiate conversation with a user" => TelegramRejectionReason.BotCannotInitiate,
                "Bad Request: message is too long" => TelegramRejectionReason.MessageTooLong,
                "Bad Request: message text is empty" => TelegramRejectionReason.MessageTextEmpty,
                _ => TelegramRejectionReason.Unrecognized
            }
            : TelegramRejectionReason.Unrecognized;
        var code = Integer(root, "error_code");
        var retry = root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
            ? Integer(parameters, "retry_after") : null;
        return new(ok, code, code == 429 || retry is > 0 ? TelegramRejectionReason.RateLimited : reason, retry);
    }

    private static int? Integer(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}

public enum TelegramRejectionReason
{
    Unrecognized,
    BotBlocked,
    UserDeactivated,
    ChatNotFound,
    BotCannotInitiate,
    MessageTooLong,
    MessageTextEmpty,
    RateLimited
}
