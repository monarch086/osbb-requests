using System.Globalization;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Admin;
using OsbbRequests.Services.Export;

namespace OsbbRequests.Services;

public sealed class RequestService(IRequestStore store, IAdministratorDirectory administrators)
{
    public async Task<Page<Assignee>> ListAssigneesAsync(AdminUser actor, string? cursor, CancellationToken ct)
    {
        RequireAssigneesAccess(actor);
        var page = await administrators.ListAdminsAsync(cursor, ct);
        return new(page.Items.Where(admin => admin.IsActive).Select(admin => new Assignee(admin.UserId, admin.Email)).ToArray(), page.Cursor);
    }

    public Task<AdminRequestPage> ListRequestsAsync(AdminUser actor, RequestSearch search, string? cursor,
        string? page, string? includeCounts, CancellationToken ct)
    {
        var filters = ValidateRequestSearch(actor, search);
        if (!int.TryParse(page ?? "1", out var pageNumber))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректна сторінка.");
        if (includeCounts is not (null or "true" or "false"))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний параметр статистики.");
        return store.ListRequestsWithStatisticsAsync(filters, cursor, pageNumber, ct, includeCounts != "false");
    }

    public Task<byte[]> ExportRequestsAsync(AdminUser actor, RequestSearch search, CancellationToken ct) =>
        new TicketExport(store).CreateAsync(ValidateRequestSearch(actor, search), ct);

    public async Task<ManagedRequest> GetManagedRequestAsync(AdminUser actor, long ownerId, long requestId, CancellationToken ct)
    {
        AdminAccess.RequireActive(actor);
        var current = await store.GetRequestAsync(ownerId, requestId, ct) ??
            throw new AdminOperationException(AdminFailure.NotFound, "Заявку не знайдено.");
        RequireRequestAccess(actor, current);
        return current;
    }

    public Task<IReadOnlyList<Dictionary<string, string>>> GetRequestHistoryAsync(AdminUser actor, ManagedRequest current, CancellationToken ct)
    {
        RequireRequestAccess(actor, current);
        return store.HistoryAsync(current.Request, ct);
    }

    public async Task<ManagedRequest> GetRequestDetailsAsync(AdminUser actor, ManagedRequest current, CancellationToken ct)
    {
        RequireRequestAccess(actor, current);
        var residents = current.Request.Category == RequestCategories.KeyOrder && current.Request.KeyItems is { Count: > 0 }
            ? await store.GetKeyResidentsAsync(current.Request, ct) : null;
        return current with
        {
            Author = await store.GetRequestAuthorAsync(current.Request, ct),
#pragma warning disable CS0618 // Populate the legacy response until older frontend versions are retired.
            KeyResidentNames = residents?.Select(group => (IReadOnlyList<string>)group.Select(resident => resident.Name).ToArray()).ToArray(),
#pragma warning restore CS0618
            KeyResidents = residents
        };
    }

    public async Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, RequestChange change, CancellationToken ct)
    {
        RequireRequestAccess(actor, current);
        if (change.Version != current.Version) throw new AdminOperationException(AdminFailure.Conflict, "Заявку змінено. Оновіть список.");
        if (!Enum.TryParse<RequestStatus>(change.Status, out var status) || !Enum.IsDefined(status) ||
            !Enum.TryParse<RequestVisibility>(change.Visibility, out var visibility) || !Enum.IsDefined(visibility) ||
            change.Priority is not ("Low" or "Normal" or "High" or "Urgent"))
        {
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректні параметри заявки.");
        }

        if (current.Request.Category == RequestCategories.KeyOrder && visibility != RequestVisibility.Private)
            throw new AdminOperationException(AdminFailure.InvalidInput, "Замовлення ключів має бути приватним.");
        if (change.Resolution?.Length > 1000) throw new AdminOperationException(AdminFailure.InvalidInput, "Пояснення має містити не більше 1000 символів.");
        if (status == RequestStatus.Rejected && current.Request.Status != RequestStatus.Rejected && string.IsNullOrWhiteSpace(change.Resolution))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Для відхилення потрібне пояснення.");
        RequestScope? scope = null;
        if (change.Scope is not null && Enum.TryParse<RequestScope>(change.Scope, out var parsedScope) && Enum.IsDefined(parsedScope)) scope = parsedScope;
        if ((change.Scope is not null && scope is null) || !ComplexLayout.IsValidRequest(current.Request.Category, scope, change.BuildingNumber, change.Floor, change.Entrance))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Оберіть будинок 4Б, 4В, 4Г або 4Д або прибудинкову територію. Для будинку оберіть поверх 1–25 або «Парадне»; під’їзд - 1, 2 або «Не зазначено» (для 4Б лише 1 або «Не зазначено»).");
        var date = Date(change.PlannedDate);
        if (!string.IsNullOrEmpty(change.AssignedTo) && (await administrators.GetAdminAsync(change.AssignedTo, ct))?.IsActive != true)
            throw new AdminOperationException(AdminFailure.InvalidInput, "Оберіть активного адміністратора.");
        var keyItems = current.Request.KeyItems;
        if (change.KeySerialNumbers is not null)
        {
            if (current.Request.Category != RequestCategories.KeyOrder || keyItems is null || change.KeySerialNumbers.Count != keyItems.Count ||
                change.KeySerialNumbers.Any(serial => serial is { Length: > 25 }))
            {
                throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректні серійні номери ключів (до 25 символів). ");
            }

            keyItems = keyItems.Select((item, index) => item with { SerialNumber = string.IsNullOrWhiteSpace(change.KeySerialNumbers[index]) ? null : change.KeySerialNumbers[index]!.Trim() }).ToArray();
        }
        var next = current.Request with
        {
            KeyItems = keyItems,
            Scope = scope,
            BuildingNumber = change.BuildingNumber,
            Floor = change.Floor,
            Entrance = change.Entrance,
            Status = status,
            Visibility = visibility,
            Resolution = string.IsNullOrWhiteSpace(change.Resolution) ? current.Request.Resolution : change.Resolution.Trim(),
            AssignedTo = string.IsNullOrEmpty(change.AssignedTo) ? null : change.AssignedTo,
            Priority = change.Priority,
            PlannedDate = date is null ? null : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        await store.UpdateRequestAsync(actor, current, next, ct, string.IsNullOrWhiteSpace(change.Resolution) ? null : change.Resolution.Trim());
    }

    private static void RequireRequestAccess(AdminUser actor, ManagedRequest current)
    {
        AdminAccess.RequireActive(actor);
        if (current.Request.Category == RequestCategories.KeyOrder ? !actor.EffectivePermissions.KeyOrders : !actor.EffectivePermissions.ComplexRequests)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для роботи із заявкою.");
    }

    private static RequestFilters ValidateRequestSearch(AdminUser actor, RequestSearch search)
    {
        AdminAccess.RequireActive(actor);
        var statuses = string.IsNullOrEmpty(search.Status) ? [] : search.Status.Split(',').Distinct(StringComparer.Ordinal).ToArray();
        if (search.Status?.Length > 150 || statuses.Any(value => !Enum.GetNames<RequestStatus>().Contains(value, StringComparer.Ordinal)))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний статус.");
        var from = Date(search.From);
        var to = Date(search.To);
        if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний період.");
        if (search.Search?.Length > 200) throw new AdminOperationException(AdminFailure.InvalidInput, "Пошук: до 200 символів.");
        var target = search.Target;
        if (target is { Length: > 0 } && target is not ("Complex" or "Unspecified") && !ComplexLayout.BuildingNumbers.Any(number => target == "Building" + number))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Оберіть будинок 4Б, 4В, 4Г або 4Д або прибудинкову територію.");
        int? floor = null;
        if (search.Floor is { Length: > 0 } floorText)
        {
            if (!int.TryParse(floorText, NumberStyles.None, CultureInfo.InvariantCulture, out var floorNumber) ||
                floorNumber is < ComplexLayout.EntranceFloor or > ComplexLayout.FloorCount)
            {
                throw new AdminOperationException(AdminFailure.InvalidInput, "Оберіть «Парадне» або поверх від 1 до 25.");
            }

            floor = floorNumber;
        }
        var section = search.Section;
        if (section is not (null or "complex" or "keys"))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний розділ заявок.");
        if (section == "complex" && !actor.EffectivePermissions.ComplexRequests || section == "keys" && !actor.EffectivePermissions.KeyOrders ||
            section is null && !(actor.EffectivePermissions.ComplexRequests && actor.EffectivePermissions.KeyOrders))
        {
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для перегляду заявок.");
        }

        return new RequestFilters(statuses, search.Category, search.Search, from, to, target, floor, section);
    }

    private static string? Date(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректна дата.");
        return value;
    }

    public static void RequireAssigneesAccess(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.EffectivePermissions.ComplexRequests && !actor.EffectivePermissions.KeyOrders)
            throw new AdminOperationException(AdminFailure.Forbidden, "Недостатньо прав для роботи із заявками.");
    }
}
