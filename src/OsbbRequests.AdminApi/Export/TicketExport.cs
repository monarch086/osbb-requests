using System.Globalization;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Export;

public sealed class TicketExport(IAdminStore store)
{
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static readonly string[] Headings = ["Номер", "Дата створення", "Статус", "Категорія", "Місце", "Поверх", "Уточнення місця", "Опис", "Автор", "Телефон", "Квартира автора", "Пріоритет", "Видимість", "Відповідальний", "Планова дата", "Пояснення"];

    public async Task<byte[]> CreateAsync(RequestFilters filters, CancellationToken ct)
    {
        var items = new List<ManagedRequest>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var page = await store.ListRequestsAsync(filters, cursor, ct);
            items.AddRange(page.Items);
            if (items.Count > 1_048_575) throw new ApiError(413, "Забагато заявок для Excel. Звузьте фільтри та спробуйте ще раз.");
            cursor = page.Cursor;
            if (cursor is not null && !cursors.Add(cursor)) throw new ApiError(503, "Не вдалося завантажити всі заявки. Спробуйте ще раз.");
        } while (cursor is not null);
        var authors = new Dictionary<long, RequestAuthor?>();
        foreach (var batch in items.DistinctBy(item => item.Request.OwnerId).Chunk(8))
        {
            ct.ThrowIfCancellationRequested();
            var profiles = await Task.WhenAll(batch.Select(item => store.GetRequestAuthorAsync(item.Request, ct)));
            for (var i = 0; i < batch.Length; i++) authors.Add(batch[i].Request.OwnerId, profiles[i]);
        }
        var rows = items.Select(item => Row(item.Request, authors[item.Request.OwnerId]));
        var file = Spreadsheet.Create("Заявки", Headings, rows, ct);
        if (file.Length > MaxFileBytes) throw new ApiError(413, "Файл завеликий. Звузьте фільтри та спробуйте ще раз.");
        return file;
    }

    private static string[] Row(MaintenanceRequest r, RequestAuthor? author)
    {
        var location = r.Scope switch
        {
            RequestScope.Complex => "Прибудинкова територія",
            RequestScope.Building => $"Будинок {r.BuildingNumber} · під’їзд {(r.Entrance?.ToString(CultureInfo.InvariantCulture) ?? "Не зазначено")}" +
                (r.Floor == 0 ? " · Парадне" : r.Floor is { } floor ? $" · поверх {floor}" : " · без поверху"),
            _ => "Будинок не уточнено"
        };
        var status = r.Status switch { RequestStatus.New => "Нова", RequestStatus.InProgress => "В роботі", RequestStatus.AwaitingClarification => "Очікує уточнення", RequestStatus.Completed => "Виконана", RequestStatus.Rejected => "Відхилена", _ => r.Status.ToString() };
        var priority = r.Priority switch { "Low" => "Низький", "Normal" => "Звичайний", "High" => "Високий", "Urgent" => "Терміновий", _ => r.Priority };
        var name = author?.PersonalDataDeleted == true ? "Мешканець видалив свої дані" : string.Join(" ", new[] { author?.FirstName, author?.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return [r.Id.ToString(CultureInfo.InvariantCulture), TimeZoneInfo.ConvertTime(r.CreatedAt, Kyiv).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture), status, r.Category, location,
            r.Floor is null ? "" : r.Floor == 0 ? "Парадне" : r.Floor.Value.ToString(CultureInfo.InvariantCulture), r.Location ?? "", r.Description, name,
            author?.PersonalDataDeleted == true ? "" : author?.Phone ?? "", author?.PersonalDataDeleted == true ? "" : author?.ApartmentNumber ?? "",
            priority, r.Visibility == RequestVisibility.Public ? "Для мешканців комплексу" : "Приватна",
            r.AssignedTo ?? "", r.PlannedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", r.Resolution ?? ""];
    }
}
