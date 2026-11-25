using System.Globalization;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services.Export;

public sealed class ResidentExport(IAdminStore store)
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<byte[]> CreateAsync(bool pending, string? building, string? residentType, string? sort, string? direction, CancellationToken ct)
    {
        var residents = await store.ListResidentExportAsync(pending, building, residentType, sort, direction, ct);
        var apartments = pending ? null : await store.ListApprovedApartmentsForExportAsync(residents, ct);
        string[] columns = ["Прізвище", "Ім’я", "Тип мешканця", "Телефон", "Email", "Будинок", "Квартира"];
        string[] headings = pending ? [.. columns, "Дата подання"] : [.. columns, "Підтверджені додаткові квартири", "Дата реєстрації"];
        var file = Spreadsheet.Create(pending ? "Очікують перевірки" : "Мешканці", headings,
            residents.Select(r => Row(r, pending, apartments?.GetValueOrDefault(r.Id))), ct);
        if (file.Length > 4 * 1024 * 1024) throw new AdminOperationException(AdminFailure.TooLarge, "Файл завеликий. Звузьте фільтри та спробуйте ще раз.");
        return file;
    }

    private static string[] Row(ResidentRegistration r, bool pending, IReadOnlyList<OwnerApartment>? apartments)
    {
        var date = pending ? r.UpdatedAt : r.ApprovedAt ?? r.UpdatedAt;
        var formatted = TimeZoneInfo.ConvertTime(date, Kyiv).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture);
        var type = r.ResidentType switch
        {
            ResidentType.Owner => "Власник",
            ResidentType.FamilyMember => "Член родини",
            ResidentType.Tenant => "Орендар",
            ResidentType.Employee => "Працівник ОСББ",
            _ => "Не зазначено"
        };
        string[] fields = r.PersonalDataDeleted
            ? ["Мешканець видалив свої дані", "", "", "", "", "", ""]
            : [r.LastName ?? "", r.FirstName ?? "", type, r.Phone ?? "", r.Email ?? "", r.BuildingNumber ?? "", r.ApartmentNumber ?? ""];
        if (pending) return [.. fields, formatted];
        var addresses = r.PersonalDataDeleted ? "" : string.Join(", ", (apartments ?? []).Select(apartment =>
            FormattableString.Invariant($"{apartment.BuildingNumber} / {apartment.ApartmentNumber}")));
        return [.. fields, addresses, formatted];
    }
}
