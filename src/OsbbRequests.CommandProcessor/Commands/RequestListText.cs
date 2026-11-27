using System.Globalization;
using OsbbRequests.Domain;
using OsbbRequests.Services.Requests;

namespace OsbbRequests.CommandProcessor.Commands;

internal static class RequestListText
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public static string DateLine(DateTimeOffset createdAt) =>
        "Подано: " + TimeZoneInfo.ConvertTime(createdAt, Kyiv).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + "\n";

    public static string LocationLine(RequestScope? scope, string? buildingNumber, int? floor, int? entrance)
    {
        if (scope == RequestScope.Complex) return RequestLocationText.ComplexLabel + "\n";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(buildingNumber)) parts.Add("Будинок " + buildingNumber);
        if (entrance is { } entry) parts.Add("під’їзд " + entry.ToString(CultureInfo.InvariantCulture));
        if (floor is { } number) parts.Add(number == ComplexLayout.EntranceFloor ? "Парадне" : "поверх " + number.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "" : string.Join(" · ", parts) + "\n";
    }

    public static string Description(string description) => "\n~~~~~\n" + Limit(description, 2000) + "\n~~~~~";

    public const int MessageLimit = 4096;
    public static int FooterLength { get; } = Footer(int.MaxValue, int.MaxValue).Length;
    public static string Footer(int page, int total) => $"\n\nСторінка {page} з {total}";

    public static string Limit(string value, int length)
    {
        if (value.Length <= length) return value;
        var end = char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
        return value[..end] + "…";
    }
}
