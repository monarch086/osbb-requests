using System.Globalization;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Export;

public sealed class ResidentExport(IAdminStore store)
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    public async Task<byte[]> CreateAsync(bool pending, string? building, string? residentType, string? sort, string? direction, CancellationToken ct)
    {
        var residents = await store.ListResidentExportAsync(pending, building, residentType, sort, direction, ct);
        string[] headings = ["Прізвище", "Ім’я", "Тип мешканця", "Телефон", "Email", "Будинок", "Квартира", pending ? "Дата подання" : "Дата реєстрації"];
        var file = Spreadsheet.Create(pending ? "Очікують перевірки" : "Мешканці", headings, residents.Select(r => Row(r, pending)), ct);
        if (file.Length > 4 * 1024 * 1024) throw new ApiError(413, "Файл завеликий. Звузьте фільтри та спробуйте ще раз.");
        return file;
    }

    private static string[] Row(ResidentRegistration r, bool pending)
    {
        var date = pending ? r.UpdatedAt : r.ApprovedAt ?? r.UpdatedAt;
        var formatted = TimeZoneInfo.ConvertTime(date, Kyiv).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture);
        if (r.PersonalDataDeleted) return ["Мешканець видалив свої дані", "", "", "", "", "", "", formatted];
        var type = r.ResidentType switch
        {
            ResidentType.Owner => "Власник",
            ResidentType.FamilyMember => "Член родини",
            ResidentType.Tenant => "Орендар",
            ResidentType.Employee => "Працівник ОСББ",
            _ => "Не зазначено"
        };
        return [r.LastName ?? "", r.FirstName ?? "", type, r.Phone ?? "", r.Email ?? "", r.BuildingNumber ?? "", r.ApartmentNumber ?? "", formatted];
    }
}
