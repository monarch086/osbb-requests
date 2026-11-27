using System.Globalization;
using System.Text.Json.Serialization;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Requests;

namespace OsbbRequests.Services.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter<AdministratorEvent>))]
public enum AdministratorEvent { NewResident, NewRequest, NewAdministrator, NewComplexRequest, NewKeyOrder, NewApartmentRegistration }

public sealed record AdministratorNotificationSettings(string? TelegramChatId = null, bool NotifyNewResidents = false,
    bool NotifyNewRequests = false, bool NotifyNewAdministrators = false, bool? NotifyNewKeyOrders = null, bool? NotifyNewApartmentRegistrations = null)
{
    public bool Includes(AdministratorEvent kind) => kind switch
    {
        AdministratorEvent.NewResident => NotifyNewResidents,
        AdministratorEvent.NewApartmentRegistration => NotifyNewApartmentRegistrations ?? NotifyNewResidents,
        AdministratorEvent.NewRequest => NotifyNewRequests,
        AdministratorEvent.NewComplexRequest => NotifyNewRequests,
        AdministratorEvent.NewKeyOrder => NotifyNewKeyOrders ?? NotifyNewRequests,
        AdministratorEvent.NewAdministrator => NotifyNewAdministrators,
        _ => false
    };
    public long? ChatId => long.TryParse(TelegramChatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) && id != 0 ? id : null;
}

public static class AdministratorNotifications
{
    private static string Date(DateTimeOffset date) => TimeZoneInfo.ConvertTime(date, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"))
        .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " (Київ)";
    private static string ResidentTypeLabel(ResidentType type) => type switch
    {
        ResidentType.Owner => "Власник",
        ResidentType.FamilyMember => "Член родини",
        ResidentType.Tenant => "Орендар",
        ResidentType.Employee => "Працівник ОСББ",
        _ => "Не зазначено"
    };

    public static ResidentNotification NewResident(ResidentProfile resident) => new(
        "new-resident-" + resident.Id + "-v" + resident.Version.ToString(CultureInfo.InvariantCulture), resident.BuildingId, 0, null, null,
        "Нова реєстрація мешканця в ЖК «Зелений острів 2».\n" +
        string.Join(" ", new[] { resident.FirstName, resident.LastName }.Where(value => !string.IsNullOrWhiteSpace(value))) +
        "\nТип мешканця: " + ResidentTypeLabel(resident.ResidentType) +
        "\nБудинок: " + (string.IsNullOrWhiteSpace(resident.BuildingNumber) ? "Не зазначено" : resident.BuildingNumber.Trim()) +
        "\nПодано: " + Date(resident.UpdatedAt) + (resident.Status == ResidentRegistrationStatus.Approved
            ? "\nРеєстрацію схвалено автоматично. Мешканцю надано доступ."
            : "\nПотребує перевірки в розділі «Мешканці» вебпанелі."), resident.UpdatedAt, AdministratorEvent.NewResident);

    public static ResidentNotification NewApartmentRegistration(string complex, OwnerApartment apartment) => new(
        "new-apartment-" + apartment.Id, complex, 0, null, null,
        "Нова заявка на реєстрацію додаткової квартири в ЖК «Зелений острів 2».\n" +
        "Подано: " + Date(apartment.SubmittedAt) +
        "\nПеревірте заявку в розділі «Мешканці» → «Додаткові квартири».",
        apartment.SubmittedAt, AdministratorEvent.NewApartmentRegistration);

    public static ResidentNotification NewRequest(MaintenanceRequest request) => new(
        "new-request-" + request.Id.ToString(CultureInfo.InvariantCulture), request.BuildingId, 0, request.Id, 1,
        $"Нова заявка №{request.Id} у ЖК «Зелений острів 2».\n" +
        RequestLocationText.Format(request.Scope, request.BuildingNumber, request.Floor, request.Entrance) + "\nКатегорія: " + request.Category +
        "\nСтворено: " + Date(request.CreatedAt) + "\n~~~~~\n" + request.Description +
        "\n~~~~~\nПерегляньте заявку в розділі «Заявки» вебпанелі.", request.CreatedAt,
        request.Category == RequestCategories.KeyOrder ? AdministratorEvent.NewKeyOrder : AdministratorEvent.NewComplexRequest);

    public static ResidentNotification NewAdministrator(string id, string email, string complex, DateTimeOffset now) => new(
        "new-administrator-" + id, complex, 0, null, null,
        "Новий обліковий запис адміністратора в ЖК «Зелений острів 2».\n" + email +
        "\nСтворено: " + Date(now) + "\nПотребує надання доступу в розділі «Адміністратори» вебпанелі.", now, AdministratorEvent.NewAdministrator);
}
