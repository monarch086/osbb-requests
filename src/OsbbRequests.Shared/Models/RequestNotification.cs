using System.Globalization;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Shared.Models;

public sealed record RequestNotification(string Id, string ComplexId, long OwnerId, long RequestId,
    int RequestVersion, string Text, DateTimeOffset CreatedAt)
{
    public static RequestNotification? Create(MaintenanceRequest previous, MaintenanceRequest next, int version, DateTimeOffset now)
    {
        if (previous.Id != next.Id || previous.OwnerId != next.OwnerId || previous.BuildingId != next.BuildingId)
            throw new InvalidOperationException("Notification request identity mismatch.");
        var oldComment = previous.Resolution?.Trim() ?? "";
        var comment = next.Resolution?.Trim() ?? "";
        var statusChanged = previous.Status != next.Status;
        var commentChanged = oldComment != comment;
        if (!statusChanged && !commentChanged) return null;
        var date = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        var text = $"Оновлення вашої заявки №{next.Id}\n" +
            $"{ComplexLayout.Label(next.Scope, next.BuildingNumber, next.Floor)}\n" +
            $"Оновлено: {date:dd.MM.yyyy HH:mm} (Київ)\n" +
            (statusChanged ? $"Статус: {Label(previous.Status)} → {Label(next.Status)}" : $"Статус: {Label(next.Status)}");
        if (comment.Length > 0) text += "\n\nПояснення для мешканця:\n" + comment;
        else if (commentChanged) text += "\n\nПояснення для мешканця видалено.";
        text += $"\n\nДеталі: /request {next.Id}";
        return new(next.Id.ToString(CultureInfo.InvariantCulture) + "-v" + version.ToString(CultureInfo.InvariantCulture),
            next.BuildingId, next.OwnerId, next.Id, version, text, now);
    }

    public static string DueKey(DateTimeOffset dueAt, string id) => "DUE#" + dueAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "#" + id;
    public static string Partition(string complexId) => "NOTIFICATIONS#" + complexId;
    private static string Label(RequestStatus status) => status switch
    {
        RequestStatus.New => "Нова",
        RequestStatus.InProgress => "В роботі",
        RequestStatus.AwaitingClarification => "Очікує уточнення",
        RequestStatus.Completed => "Виконана",
        RequestStatus.Rejected => "Відхилена",
        _ => throw new InvalidOperationException("Unknown request status.")
    };
}
