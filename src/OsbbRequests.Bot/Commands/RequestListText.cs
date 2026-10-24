namespace OsbbRequests.Bot.Commands;

internal static class RequestListText
{
    public static string Build(string heading, IReadOnlyList<string> entries, Func<int, string?> nextCommand)
    {
        var text = heading;
        var count = 0;
        foreach (var entry in entries)
        {
            var candidate = text + "\n\n" + entry;
            if (candidate.Length + Footer(nextCommand(count + 1)).Length > 4096)
                break;
            text = candidate;
            count++;
        }
        if (entries.Count > 0 && count == 0) throw new InvalidOperationException("Request list entry exceeds Telegram message limit.");
        return text + Footer(nextCommand(count));
    }

    public static string Limit(string value, int length)
    {
        if (value.Length <= length) return value;
        var end = char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
        return value[..end] + "…";
    }
    private static string Footer(string? command) => command is null ? "" : "\n\nНаступна сторінка: " + command;
}
