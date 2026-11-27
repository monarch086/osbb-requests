using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;
using StoredRequest = OsbbRequests.DataImport.Storage.Request;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task<Page<ManagedRequest>> ListRequestsAsync(RequestFilters filters, string? cursor, CancellationToken ct) =>
        Read((db, current) => RequestPageAsync(db, current, FilterRequests(db, current, filters), Offset(cursor), ct), ct);

    public Task<AdminRequestPage> ListRequestsWithStatisticsAsync(RequestFilters filters, string? cursor, int pageNumber,
        CancellationToken ct, bool includeCounts = true) => Read(async (db, current) =>
    {
        if (pageNumber is < 1 or > 1000000) throw Error(AdminFailure.InvalidInput, "Некоректна сторінка.");
        var query = FilterRequests(db, current, filters);
        var page = await RequestPageAsync(db, current, query, Offset(cursor), ct);
        RequestCounts? counts = null;
        if (includeCounts)
        {
            var groups = await query.GroupBy(row => row.Status).Select(group => new { Status = group.Key, Count = group.Count() })
                .ToDictionaryAsync(group => group.Status, group => group.Count, ct);
            counts = new(groups.Values.Sum(), groups.GetValueOrDefault(RequestStatus.New), groups.GetValueOrDefault(RequestStatus.InProgress));
        }
        return new AdminRequestPage(page.Items, page.Cursor, pageNumber, PageSize, counts);
    }, ct);

    private IQueryable<StoredRequest> FilterRequests(DataContext db, Scope current, RequestFilters filters)
    {
        var query = Requests(db, current);
        if (filters.Statuses is { Count: > 0 })
        {
            var statuses = filters.Statuses.Select(Enum.Parse<RequestStatus>).ToArray();
            query = query.Where(row => statuses.Contains(row.Status));
        }
        if (!string.IsNullOrEmpty(filters.Category)) query = query.Where(row => row.Category == filters.Category);
        if (filters.Section == "keys") query = query.Where(row => row.Category == RequestCategories.KeyOrder);
        if (filters.Section == "complex") query = query.Where(row => row.Category != RequestCategories.KeyOrder);
        if (filters.Floor is { } floor) query = query.Where(row => row.Floor == floor);
        if (filters.Target == "Complex") query = query.Where(row => row.Scope == RequestScope.Complex);
        else if (filters.Target == "Unspecified")
            query = query.Where(row => row.Scope != RequestScope.Complex && (row.Scope != RequestScope.Building ||
                row.BuildingNumber == null || !ComplexLayout.BuildingNumbers.Contains(row.BuildingNumber)));
        else if (filters.Target is { Length: > 8 } target)
            query = query.Where(row => row.Scope == RequestScope.Building && row.BuildingNumber == target.Substring(8));
        if (Boundary(filters.From, TimeOnly.MinValue) is { } start) query = query.Where(row => row.CreatedAt >= start);
        if (Boundary(filters.To, TimeOnly.MaxValue) is { } end) query = query.Where(row => row.CreatedAt <= end);
        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var pattern = "%" + filters.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(row => EF.Functions.ILike(row.Description ?? "", pattern) || EF.Functions.ILike(row.Location ?? "", pattern));
        }
        return query;
    }

    private static DateTimeOffset? Boundary(string? value, TimeOnly time) => value is null ? null :
        new DateTimeOffset(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(time), TimeSpan.Zero);

    private async Task<Page<ManagedRequest>> RequestPageAsync(DataContext db, Scope current, IQueryable<StoredRequest> query,
        int offset, CancellationToken ct)
    {
        var rows = await query.OrderByDescending(row => row.Priority == "Urgent" ? 3 : row.Priority == "High" ? 2 : row.Priority == "Low" ? 0 : 1)
            .ThenByDescending(row => row.CreatedAt).ThenByDescending(row => row.RequestId).ThenBy(row => row.OwnerId)
            .Skip(offset).Take(PageSize + 1).ToListAsync(ct);
        var visible = rows.Take(PageSize).ToArray();
        var ids = visible.Select(row => row.RequestId).ToArray();
        var keys = await db.RequestKeyItems.Where(key => key.SourceId == current.SourceId && ids.Contains(key.RequestId))
            .OrderBy(key => key.Position).ToListAsync(ct);
        var grouped = keys.ToLookup(key => (key.OwnerId, key.RequestId));
        return new(visible.Select(row => ToManaged(row, grouped[(row.OwnerId, row.RequestId)])).ToArray(), NextCursor(offset, rows.Count));
    }

    public Task<ManagedRequest?> GetRequestAsync(long ownerId, long requestId, CancellationToken ct) => Read<ManagedRequest?>(async (db, current) =>
    {
        var row = await Requests(db, current).SingleOrDefaultAsync(row => row.OwnerId == ownerId && row.RequestId == requestId, ct);
        return row is null ? null : ToManaged(row, await KeyRows(db, row).ToListAsync(ct));
    }, ct);

    public Task<RequestAuthor?> GetRequestAuthorAsync(MaintenanceRequest request, CancellationToken ct) => Read<RequestAuthor?>(async (db, current) =>
    {
        if (request.BuildingId != options.Complex) return null;
        var profile = await ResidentByUserAsync(db, current, request.OwnerId, ct);
        if (profile is null) return null;
        return profile.HasDeletedPersonalData ? new(null, null, null, null, true) :
            new(profile.FirstName, profile.LastName, profile.BuildingNumber, profile.ApartmentNumber, Phone: profile.Phone);
    }, ct);

    public Task<IReadOnlyList<IReadOnlyList<KeyResident>>> GetKeyResidentsAsync(MaintenanceRequest request, CancellationToken ct) =>
        Read<IReadOnlyList<IReadOnlyList<KeyResident>>>(async (db, current) =>
        {
            if (request.BuildingId != options.Complex) return [];
            var phones = (request.KeyItems ?? []).Select(item => item.Phone).ToArray();
            var matches = await KeyProfilesAsync(db, current, phones, ct);
            return phones.Select(phone => (IReadOnlyList<KeyResident>)matches[KeyPhone(phone)].Select(ToKeyResident).ToArray()).ToArray();
        }, ct);

    public Task UpdateRequestAsync(AdminUser actor, ManagedRequest current, MaintenanceRequest next, CancellationToken ct, string? comment = null) =>
        Write(async (db, area) =>
        {
            await CheckActor(db, area, actor, ct);
            var row = await Requests(db, area).SingleOrDefaultAsync(value => value.OwnerId == current.Request.OwnerId && value.RequestId == current.Request.Id, ct)
                ?? throw Error(AdminFailure.NotFound, "Заявку не знайдено.");
            if (row.Version != current.Version) throw Error(AdminFailure.Conflict, "Заявку змінено. Оновіть список.");
            if (next.BuildingId != options.Complex || next.Id != row.RequestId || next.OwnerId != row.OwnerId)
                throw Error(AdminFailure.InvalidInput, "Некоректна заявка.");
            if (row.Category == RequestCategories.KeyOrder ? !actor.EffectivePermissions.KeyOrders : !actor.EffectivePermissions.ComplexRequests)
                throw Error(AdminFailure.Forbidden, "Недостатньо прав для роботи із заявкою.");
            if (!ComplexLayout.IsValidRequest(next.Category, next.Scope, next.BuildingNumber, next.Floor, next.Entrance))
                throw Error(AdminFailure.InvalidInput, "Некоректна прив’язка заявки.");
            if (next.AssignedTo is { } assignee && (await ReadAdminAsync(db, area, assignee, ct))?.IsActive != true)
                throw Error(AdminFailure.Conflict, "Відповідального адміністратора змінено. Оновіть список.");
            var keys = await KeyRows(db, row).ToListAsync(ct);
            if (next.KeyItems is { } changedKeys)
                for (var index = 0; index < keys.Count; index++) keys[index].SerialNumber = changedKeys[index].SerialNumber;

            row.Status = next.Status;
            row.Visibility = next.Visibility;
            row.Resolution = next.Resolution;
            row.Priority = next.Priority;
            row.PlannedDate = next.PlannedDate;
            row.AssignedTo = next.AssignedTo;
            row.Scope = next.Scope;
            row.BuildingNumber = next.BuildingNumber;
            row.Floor = next.Floor;
            row.Entrance = next.Entrance;
            row.Version++;
            var now = DateTimeOffset.UtcNow;
            AddRequestEvent(db, row, now, actor.Email, current.Request.Status, comment,
                RequestEventSnapshot.Capture(current.Request, next));
            Enqueue(db, area, ResidentNotification.Create(current.Request, next, row.Version, now, comment));
        }, ct);

    public Task<IReadOnlyList<Dictionary<string, string>>> HistoryAsync(MaintenanceRequest request, CancellationToken ct) =>
        Read<IReadOnlyList<Dictionary<string, string>>>(async (db, current) =>
        {
            if (request.BuildingId != options.Complex) throw Error(AdminFailure.NotFound, "Заявку не знайдено.");
            var rows = await db.RequestEvents.Where(row => row.SourceId == current.SourceId && row.RequestId == request.Id)
                .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.EventKey).Take(100).ToListAsync(ct);
            var profile = await ResidentByUserAsync(db, current, request.OwnerId, ct);
            var creator = profile?.HasDeletedPersonalData == true ? "Мешканець видалив свої дані" : profile is null ? "Мешканець" : ResidentName(profile);
            return rows.Select(row => HistoryItem(row, creator)).ToArray();
        }, ct);

    private static Dictionary<string, string> HistoryItem(RequestEvent row, string creator)
    {
        var item = new Dictionary<string, string>();
        AddHistoryValue(item, "action", row.EventKey == "EVENT#CREATED" ? "Created" : row.Action);
        AddHistoryValue(item, "actor", row.EventKey == "EVENT#CREATED" || row.Action == "ResidentComment" ? creator : row.Actor);
        AddHistoryValue(item, "createdAt", row.CreatedAt?.ToString("O", CultureInfo.InvariantCulture));
        AddHistoryValue(item, "status", row.Status);
        AddHistoryValue(item, "previousStatus", row.PreviousStatus);
        AddHistoryValue(item, "target", row.Target);
        AddHistoryValue(item, "changes", row.Snapshot?.SerializeForHistory());
        AddHistoryValue(item, "resolution", HistoryValue(row.HasResolution, row.Resolution));
        var (floorField, entranceField) = row.Key.StartsWith("LOCAL#", StringComparison.Ordinal)
            ? ("Floor", "Entrance") : ("floor", "entrance");
        AddHistoryValue(item, floorField,
            HistoryValue(row.HasFloor, row.Floor?.ToString(CultureInfo.InvariantCulture)));
        AddHistoryValue(item, entranceField,
            HistoryValue(row.HasEntrance, row.Entrance?.ToString(CultureInfo.InvariantCulture)));
        return item;
    }

    private static void AddHistoryValue(Dictionary<string, string> item, string field, string? value)
    {
        if (value is not null) item[field] = value;
    }

    private static string? HistoryValue(bool present, string? value) => present ? value ?? "" : null;

    private static IQueryable<StoredRequest> Requests(DataContext db, Scope current) =>
        db.Requests.Where(row => row.SourceId == current.SourceId && row.ComplexId == current.ComplexId);

    private static IOrderedQueryable<RequestKeyItem> KeyRows(DataContext db, StoredRequest row) =>
        db.RequestKeyItems.Where(key => key.SourceId == row.SourceId && key.OwnerId == row.OwnerId && key.RequestId == row.RequestId)
            .OrderBy(key => key.Position);

    private ManagedRequest ToManaged(StoredRequest row, IEnumerable<RequestKeyItem> keys)
    {
        var items = keys.Select(key => new KeyOrderItem(key.Type, key.Phone, key.SerialNumber)).ToArray();
        return new(new MaintenanceRequest(row.RequestId, row.OwnerId, options.Complex, row.Category ?? "", row.Location ?? "",
            row.Description ?? "", row.CreatedAt, row.Status, row.Visibility, row.Resolution, row.AssignedTo,
            row.Priority, row.PlannedDate, row.Scope, row.BuildingNumber, row.Floor, row.Entrance, row.KeyType,
            row.KeyQuantity, items.Length == 0 ? null : items), row.Version);
    }

    private static void AddRequestEvent(DataContext db, StoredRequest row, DateTimeOffset now, string actor,
        RequestStatus previousStatus, string? resolution = null, RequestEventSnapshot? snapshot = null)
    {
        var key = EventKey();
        db.RequestEvents.Add(new()
        {
            SourceId = row.SourceId,
            RequestId = row.RequestId,
            Key = key,
            EventKey = key,
            CreatedAt = now,
            Actor = actor,
            Status = row.Status.ToString(),
            PreviousStatus = previousStatus.ToString(),
            Resolution = resolution,
            HasResolution = snapshot is not null,
            Snapshot = snapshot
        });
    }
}
