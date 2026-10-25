using System.Globalization;
using System.Text.Json.Serialization;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter<AdministratorEvent>))]
public enum AdministratorEvent { NewResident, NewRequest, NewAdministrator }

public sealed record AdministratorNotificationSettings(string? TelegramChatId = null, bool NotifyNewResidents = false,
    bool NotifyNewRequests = false, bool NotifyNewAdministrators = false)
{
    public bool Includes(AdministratorEvent kind) => kind switch
    {
        AdministratorEvent.NewResident => NotifyNewResidents,
        AdministratorEvent.NewRequest => NotifyNewRequests,
        AdministratorEvent.NewAdministrator => NotifyNewAdministrators,
        _ => false
    };
    public long? ChatId => long.TryParse(TelegramChatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0 ? id : null;
}

public static class AdministratorNotifications
{
    private static string Date(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"))
        .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " (Київ)";

    public static ResidentNotification NewResident(ResidentRegistration resident) => new(
        "new-resident-" + resident.Id + "-v" + resident.Version.ToString(CultureInfo.InvariantCulture), resident.BuildingId, 0, null, null,
        "Нова реєстрація мешканця в ЖК «Зелений острів 2».\n" +
        string.Join(" ", new[] { resident.FirstName, resident.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))) +
        "\nПодано: " + Date(resident.UpdatedAt) + (resident.Stage == RegistrationStage.Approved
            ? "\nРеєстрацію схвалено автоматично. Мешканцю надано доступ."
            : "\nПотребує перевірки в розділі «Мешканці» вебпанелі."), resident.UpdatedAt, AdministratorEvent.NewResident);

    public static ResidentNotification NewRequest(MaintenanceRequest request) => new(
        "new-request-" + request.Id.ToString(CultureInfo.InvariantCulture), request.BuildingId, 0, request.Id, 1,
        $"Нова заявка №{request.Id} у ЖК «Зелений острів 2».\n" +
        ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor, request.Entrance) + "\nКатегорія: " + request.Category +
        "\nСтворено: " + Date(request.CreatedAt) + "\nПерегляньте заявку в розділі «Заявки» вебпанелі.", request.CreatedAt, AdministratorEvent.NewRequest);

    public static ResidentNotification NewAdministrator(string id, string email, string complex, DateTimeOffset now) => new(
        "new-administrator-" + id, complex, 0, null, null,
        "Новий обліковий запис адміністратора в ЖК «Зелений острів 2».\n" + email +
        "\nСтворено: " + Date(now) + "\nПотребує надання доступу в розділі «Адміністратори» вебпанелі.", now, AdministratorEvent.NewAdministrator);
}
