using System.Globalization;
using System.Text;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class ComplexRequestsService(ICommandRepository repository, string complexId)
{
    public async Task<CommandOutcome> ListAsync(string arguments, CancellationToken ct)
    {
        string? before = null;
        if (arguments.Length > 0 && !TryCursor(arguments, out before))
            return new("Скористайтеся /complex або командою наступної сторінки зі списку заявок комплексу.");
        var page = await repository.ListPublicRequestsAsync(complexId, before, ct);
        var text = page.Items.Count == 0
            ? "На цій сторінці немає відкритих заявок для мешканців комплексу."
            : "Заявки комплексу — нові та в роботі:\n\n" + string.Join("\n\n", page.Items.Select(Format));
        if (page.NextBefore is { } next)
            text += "\n\nНаступна сторінка: /complex " + Encode(next);
        return new(text);
    }

    private static string Format(PublicRequestSummary request)
    {
        var date = TimeZoneInfo.ConvertTime(request.CreatedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        return $"№{request.Id} — {CommandHandler.StatusLabel(request.Status)}\n" +
            $"Подано: {date:dd.MM.yyyy HH:mm} (Київ)\n" +
            $"{ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor)}\n" +
            $"{Limit(request.Category, 80)}" +
            (string.IsNullOrEmpty(request.Location) ? "" : ": " + Limit(request.Location, 200));
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool TryCursor(string value, out string? before)
    {
        before = null;
        if (value.Length > 160 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = decoded.Split('#');
            if (parts.Length != 2 || !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
                return false;
            before = decoded;
            return true;
        }
        catch (FormatException) { return false; }
    }
}
