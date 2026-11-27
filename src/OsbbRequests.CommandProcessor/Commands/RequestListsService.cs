using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Persistence;

namespace OsbbRequests.CommandProcessor.Commands;

internal sealed class RequestListsService(ICommandRepository repository, string complexId)
{
    private const string Changed = "Список заявок змінився. Відкрийте його знову з головного меню.";

    public async Task<CommandOutcome> ListAsync(long userId, bool own, string arguments, RequestListNavigation? navigation, CancellationToken ct)
    {
        long? ownerId = own ? userId : null;
        var heading = own ? "Ваші заявки:" : "Заявки комплексу - нові та в роботі:";
        var command = own ? "/my" : "/complex";
        if (navigation is not null)
            return await ReadSavedPageAsync(ownerId, heading, command, navigation, ct);

        var (boundary, error) = await ReadBoundaryAsync(userId, own, arguments, ct);
        if (error is not null) return new(error);

        var pages = new List<long[]>();
        var ids = new List<long>();
        var firstPage = new List<string>();
        var length = heading.Length;
        var maxItems = own ? 10 : 5;
        await foreach (var item in repository.ReadRequestListAsync(complexId, ownerId, boundary, null, ct))
        {
            var text = Format(item, own);
            if (ids.Count == maxItems || length + 2 + text.Length + RequestListText.FooterLength > RequestListText.MessageLimit)
            {
                pages.Add(ids.ToArray());
                ids.Clear();
                length = heading.Length;
            }
            ids.Add(item.Id);
            length += 2 + text.Length;
            if (pages.Count == 0) firstPage.Add(text);
        }

        if (ids.Count == 0)
        {
            return new(own ? "Заявок на цій сторінці немає. Створити заявку: /new."
                : "На цій сторінці немає відкритих заявок для мешканців комплексу.");
        }

        pages.Add(ids.ToArray());
        return Reply(heading, firstPage, command, pages.ToArray(), index: 0);
    }

    private async Task<CommandOutcome> ReadSavedPageAsync(long? ownerId, string heading, string command,
        RequestListNavigation navigation, CancellationToken ct)
    {
        var index = navigation.NextPageIndex;
        if (index < 1 || index >= navigation.Pages.Length) return new(Changed);

        var lines = new List<string>();
        await foreach (var item in repository.ReadRequestListAsync(complexId, ownerId, null, navigation.Pages[index], ct))
            lines.Add(Format(item, ownerId is not null));

        var reply = Reply(heading, lines, command, navigation.Pages, index);
        return lines.Count == 0 || reply.Reply.Length > RequestListText.MessageLimit ? new(Changed) : reply;
    }

    private static CommandOutcome Reply(string heading, List<string> lines, string command, long[][] pages, int index)
    {
        var text = heading + "\n\n" + string.Join("\n\n", lines);
        if (pages.Length > 1) text += RequestListText.Footer(index + 1, pages.Length);
        return index + 1 < pages.Length
            ? new(text, NextPageCommand: command, RequestList: new(index + 1, pages))
            : new(text);
    }

    private async Task<(RequestListBoundary? Boundary, string? Error)> ReadBoundaryAsync(long userId, bool own, string arguments, CancellationToken ct)
    {
        if (arguments.Length == 0) return (null, null);
        if (own)
        {
            if (long.TryParse(arguments, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return (new(id), null);
            return (null, "Використайте /my, щоб переглянути власні заявки.");
        }

        string? before;
        var normalized = arguments.ToLowerInvariant();
        if (ComplexRequestCursor.IsShortToken(normalized))
        {
            before = await repository.GetComplexCursorAsync(userId, complexId, normalized, ct);
            if (before is null) return (null, "Цю сторінку не знайдено. Почніть перегляд із /complex.");
        }
        else if (!TryCursor(arguments, out before))
        {
            return (null, "Скористайтеся /complex, щоб переглянути заявки комплексу.");
        }

        var parts = before!.Split('#');
        return parts.Length == 2 && DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var requestId)
            ? (new(requestId, date), null)
            : (null, "Цю сторінку не знайдено. Почніть перегляд із /complex.");
    }

    private static string Format(RequestListItem request, bool own) =>
        $"№{request.Id} - {CommandHandler.StatusLabel(request.Status)}\n" +
        RequestListText.DateLine(request.CreatedAt) +
        RequestListText.LocationLine(request.Scope, request.BuildingNumber, request.Floor, request.Entrance) +
        RequestListText.Limit(request.Category, 80) +
        (string.IsNullOrEmpty(request.Location) ? "" : ": " + RequestListText.Limit(request.Location, 200)) +
        RequestListText.Description(request.Description) + (own ? $"\n/request {request.Id}" : "");

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
            {
                return false;
            }

            before = decoded;
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException) { return false; }
    }
}
