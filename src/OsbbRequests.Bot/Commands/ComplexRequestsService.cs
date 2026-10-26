using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class ComplexRequestsService(ICommandRepository repository, string complexId)
{
    public async Task<CommandOutcome> ListAsync(long userId, string arguments, CancellationToken ct)
    {
        string? before = null;
        var normalized = arguments.ToLowerInvariant();
        if (ComplexRequestCursor.IsShortToken(normalized))
        {
            before = await repository.GetComplexCursorAsync(userId, complexId, normalized, ct);
            if (before is null) return new("Цю сторінку не знайдено. Почніть перегляд із /complex.");
        }
        else if (arguments.Length > 0 && !TryCursor(arguments, out before))
            return new("Скористайтеся /complex або командою наступної сторінки зі списку заявок комплексу.");
        var page = await repository.ListPublicRequestsAsync(complexId, before, ct);
        var token = RandomNumberGenerator.GetString(ComplexRequestCursor.Alphabet, 10);
        string? nextBefore = null;
        string? NextCommand(int count)
        {
            nextBefore = count > 0 && count < page.Items.Count
                ? page.Items[count - 1].CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#" + page.Items[count - 1].Id.ToString("D20", CultureInfo.InvariantCulture)
                : page.NextBefore;
            return nextBefore is null ? null : "/complex " + token;
        }
        var text = RequestListText.Build(page.Items.Count == 0
            ? "На цій сторінці немає відкритих заявок для мешканців комплексу."
            : "Заявки комплексу - нові та в роботі:", page.Items.Select(Format).ToList(), NextCommand);
        return new(text, Pagination: nextBefore is null ? null : new(userId, complexId, token, nextBefore));
    }

    private static string Format(PublicRequestSummary request)
    {
        var date = TimeZoneInfo.ConvertTime(request.CreatedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        return $"№{request.Id} - {CommandHandler.StatusLabel(request.Status)}\n" +
            $"Подано: {date:dd.MM.yyyy HH:mm}\n" +
            $"{ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor, request.Entrance)}\n" +
            $"{RequestListText.Limit(request.Category, 80)}" +
            (string.IsNullOrEmpty(request.Location) ? "" : ": " + RequestListText.Limit(request.Location, 200)) + "\n" + RequestListText.Limit(request.Description, 2000);
    }

    private static bool TryCursor(string value, out string? before)
    {
        before = null;
        if (value.Length > 160 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return false;
        try
        {
            if (value.StartsWith("p_", StringComparison.Ordinal))
            {
                if (value.Length != 26) return false;
                var bytes = Convert.FromBase64String(value[2..].Replace('-', '+').Replace('_', '/'));
                if (bytes.Length != 18) return false;
                var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
                var compactId = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8));
                var offset = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(16));
                if (compactId < 0 || offset is < -840 or > 840) return false;
                var date = new DateTimeOffset(ticks, TimeSpan.FromMinutes(offset));
                before = date.ToString("O", CultureInfo.InvariantCulture) + "#" + compactId.ToString("D20", CultureInfo.InvariantCulture);
                return true;
            }
            // Accept commands already sent before the compact format was introduced.
            var padded = value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = decoded.Split('#');
            if (parts.Length != 2 || !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
                return false;
            before = decoded;
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException) { return false; }
    }
}
