using System.Globalization;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Shared.Models;

public sealed record ResidentNotification(string Id, string ComplexId, long OwnerId, long? RequestId,
    int? RequestVersion, string Text, DateTimeOffset CreatedAt, AdministratorEvent? AdministratorEvent = null, string? AdministratorId = null, string? ResidentBroadcastId = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBroadcast => OwnerId == 0 && AdministratorId is null && (AdministratorEvent is not null || ResidentBroadcastId is not null);

    public static ResidentNotification? Create(MaintenanceRequest previous, MaintenanceRequest next, int version, DateTimeOffset now)
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
            $"{ComplexLayout.Label(next.Scope, next.BuildingNumber, next.Floor, next.Entrance)}\n" +
            $"Оновлено: {date:dd.MM.yyyy HH:mm} (Київ)\n" +
            (statusChanged ? $"Статус: {Label(previous.Status)} → {Label(next.Status)}" : $"Статус: {Label(next.Status)}");
        if (comment.Length > 0) text += "\n\nПояснення для мешканця:\n" + comment;
        else if (commentChanged) text += "\n\nПояснення для мешканця видалено.";
        text += $"\n\nДеталі: /request {next.Id}";
        return new(next.Id.ToString(CultureInfo.InvariantCulture) + "-v" + version.ToString(CultureInfo.InvariantCulture),
            next.BuildingId, next.OwnerId, next.Id, version, text, now);
    }

    public static ResidentNotification RegistrationApproved(ResidentRegistration registration)
    {
        if (registration.Stage != RegistrationStage.Approved || registration.UserId <= 0)
            throw new InvalidOperationException("Notification requires an approved registration.");
        var date = TimeZoneInfo.ConvertTime(registration.UpdatedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        var text = "Вашу реєстрацію в ЖК «Зелений острів 2» схвалено.\n" +
            "Вам надано доступ до бота комплексу.\n" +
            $"Підтверджено: {date:dd.MM.yyyy HH:mm} (Київ)\n\n" +
            "Створити заявку: /new\nВаші заявки: /my\nЗаявки комплексу: /complex";
        return new("registration-" + registration.Id + "-v" + registration.Version.ToString(CultureInfo.InvariantCulture),
            registration.BuildingId, registration.UserId, null, null, text, registration.UpdatedAt);
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
