using System.Globalization;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

internal static class RequestListText
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public static string DateLine(DateTimeOffset createdAt) =>
        "Подано: " + TimeZoneInfo.ConvertTime(createdAt, Kyiv).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + "\n";

    public static string LocationLine(RequestScope? scope, string? buildingNumber, int? floor, int? entrance)
    {
        if (scope == RequestScope.Complex) return "Весь комплекс\n";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(buildingNumber)) parts.Add("Будинок " + buildingNumber);
        if (entrance is { } entry) parts.Add("під’їзд " + entry.ToString(CultureInfo.InvariantCulture));
        if (floor is { } number) parts.Add(number == ComplexLayout.EntranceFloor ? "Парадне" : "поверх " + number.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "" : string.Join(" · ", parts) + "\n";
    }

    public static string Description(string description) => "\n~~~~~\n" + Limit(description, 2000) + "\n~~~~~";

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
