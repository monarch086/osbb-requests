namespace OsbbRequests.Bot.Commands;

public sealed record BotCommand(string Name, string Arguments)
{
    public static BotCommand? Parse(string text, string botUsername)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return null;

        var end = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var token = end < 0 ? text[1..] : text[1..end];
        var parts = token.Split('@');
        if (parts.Length > 2 || parts.Length == 2 &&
            !string.Equals(parts[1], botUsername, StringComparison.OrdinalIgnoreCase))
            return null;

        if (parts[0].Length == 0)
            return null;

        return new BotCommand(parts[0].ToLowerInvariant(), end < 0 ? "" : text[end..].Trim());
    }
}
