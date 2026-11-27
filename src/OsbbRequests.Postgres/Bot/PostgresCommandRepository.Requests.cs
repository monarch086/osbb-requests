using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Domain.Residents;
using OsbbRequests.Services.Notifications;
using OsbbRequests.Services.Requests;
using OsbbRequests.Services.Residents;

namespace OsbbRequests.Postgres.Bot;

public sealed partial class PostgresCommandRepository
{
    private static string AuditTarget(RequestScope? scope, string? buildingNumber) => scope == RequestScope.Complex ? "Complex" :
        scope == RequestScope.Building && buildingNumber is not null && ComplexLayout.BuildingNumbers.Contains(buildingNumber, StringComparer.Ordinal) ? "Building" + buildingNumber : "Unspecified";

    public async Task<DeletableRequest?> GetDeletableRequestAsync(long userId, long requestId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await FindRequestAsync(db, userId, requestId, ct);
        return row is null ? null : new(row.RequestId, row.OwnerId, complex, row.Status, row.Version);
    }

    public async Task<MaintenanceRequest?> GetRequestAsync(long userId, long requestId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await FindRequestAsync(db, userId, requestId, ct);
        return row is null ? null : (await ToRequestsAsync(db, [row], ct))[0];
    }

    public async Task<RequestPage> ListRequestsAsync(long userId, long? beforeId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await Requests(db).Where(row => row.OwnerId == userId && (beforeId == null || row.RequestId < beforeId))
            .OrderByDescending(row => row.RequestId).Take(11).ToListAsync(ct);
        var requests = await ToRequestsAsync(db, rows.Take(10).ToArray(), ct);
        return new(requests, rows.Count > 10 ? requests[^1].Id : null);
    }

    public async Task<PublicRequestPage> ListPublicRequestsAsync(string complexId, string? before, CancellationToken ct)
    {
        if (complexId != complex) return new([], null);
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = Requests(db).Where(row => row.Visibility == RequestVisibility.Public &&
            (row.Status == RequestStatus.New || row.Status == RequestStatus.InProgress));
        if (before is not null)
        {
            var separator = before.LastIndexOf('#');
            if (separator <= 0 || !TryParseBoundary(before, separator, out var date, out var id))
                return new([], null);
            var boundary = date.ToUniversalTime();
            query = query.Where(row => row.CreatedAt < boundary || row.CreatedAt == boundary && row.RequestId < id);
        }
        var rows = await query.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.RequestId).Take(6).ToListAsync(ct);
        var items = rows.Take(5).Select(row => new PublicRequestSummary(row.RequestId, row.Status, row.CreatedAt, row.Scope,
            row.BuildingNumber, row.Floor, row.Category ?? "", row.Location ?? "", row.Description ?? "", row.Entrance,
            FormattableString.Invariant($"{row.CreatedAt:O}#{row.RequestId}"))).ToArray();
        return new(items, rows.Count > 5 ? items[^1].CursorKey : null);
    }

    private static bool TryParseBoundary(string cursor, int separator, out DateTimeOffset date, out long id)
    {
        id = 0;
        return DateTimeOffset.TryParseExact(cursor.AsSpan(0, separator), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date) &&
            long.TryParse(cursor.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public async Task<long?> GetNotificationRequestAsync(long userId, long messageId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = FormattableString.Invariant($"NOTIFICATIONS#{complex}\u001fMESSAGE#{userId}#{messageId}");
        var localKey = scope.ReplyKey(userId, messageId);
        var now = clock.GetUtcNow();
        var requestId = await db.Notifications.Where(row => row.SourceId == sourceId && row.ComplexId == complexId &&
                row.Key == localKey && row.ExpiresAt > now).Select(row => row.RequestId).SingleOrDefaultAsync(ct);
        return requestId ?? await db.Notifications.Where(row => row.SourceId == sourceId && row.ComplexId == complexId && row.Key == key)
            .Select(row => row.RequestId).SingleOrDefaultAsync(ct);
    }

    private async Task WriteRequestsAsync(DataContext db, CommandEnvelope command, CommandOutcome outcome, CancellationToken ct)
    {
        if (outcome.NewRequest is { } request)
            await AddRequestAsync(db, command, request, outcome.OwnerProfileVersion, ct);
        if (outcome.Deletion is { } deletion)
            await DeleteRequestAsync(db, command, deletion, ct);
        if (outcome.Comment is { } comment)
            await AddCommentAsync(db, command, comment, ct);
    }

    private async Task AddRequestAsync(DataContext db, CommandEnvelope command, MaintenanceRequest request, int? profileVersion, CancellationToken ct)
    {
        CheckOwner(command, request.OwnerId, request.BuildingId);
        if (request.Id != command.UpdateId || !ComplexLayout.IsValidRequest(request.Category, request.Scope,
            request.BuildingNumber, request.Floor, request.Entrance))
        {
            throw new InvalidOperationException("Invalid new request.");
        }

        await RequireMembershipAsync(db, command.UserId, ct);
        if (request.Category == RequestCategories.KeyOrder)
            await ValidateKeyOrderAsync(db, request, profileVersion, ct);
        db.Requests.Add(new Request
        {
            SourceId = sourceId,
            ComplexId = complexId,
            OwnerId = request.OwnerId,
            RequestId = request.Id,
            Version = 1,
            Status = request.Status,
            Visibility = request.Visibility,
            CreatedAt = request.CreatedAt,
            Category = request.Category,
            Location = request.Location,
            Description = request.Description,
            Scope = request.Scope,
            BuildingNumber = request.BuildingNumber,
            Floor = request.Floor,
            Entrance = request.Entrance,
            KeyType = request.KeyType,
            KeyQuantity = request.KeyQuantity
        });
        if (request.KeyItems is { } keys)
        {
            db.RequestKeyItems.AddRange(keys.Select((key, index) => new RequestKeyItem
            {
                SourceId = sourceId,
                OwnerId = request.OwnerId,
                RequestId = request.Id,
                Position = index,
                Type = key.Type,
                Phone = PhoneNumber.Normalize(key.Phone)
            }));
        }

        var audit = CreateRequestEvent(request.Id, "EVENT#CREATED", request.CreatedAt);
        audit.ActorId = request.OwnerId;
        audit.Status = request.Status.ToString();
        audit.Target = AuditTarget(request.Scope, request.BuildingNumber);
        audit.Floor = request.Floor;
        audit.HasFloor = true;
        audit.Entrance = request.Entrance;
        audit.HasEntrance = true;
        db.RequestEvents.Add(audit);
        Enqueue(db, AdministratorNotifications.NewRequest(request));
    }

    private async Task ValidateKeyOrderAsync(DataContext db, MaintenanceRequest request, int? profileVersion, CancellationToken ct)
    {
        var owner = await ResidentAsync(db, request.OwnerId, ct);
        if (owner is not { Stage: RegistrationStage.Approved, ResidentType: ResidentType.Owner, PersonalDataDeleted: false } ||
            owner.Version != profileVersion || request.Visibility != RequestVisibility.Private || request.KeyQuantity is not >= 1)
        {
            throw new InvalidOperationException("Invalid key order authorization.");
        }

        if (request.KeyItems is not { } keys)
        {
            if (request.KeyType is not ("Магніт" or "Перепустка")) throw new InvalidOperationException("Invalid key type.");
            return;
        }
        if (keys.Count != request.KeyQuantity || keys.Count > 30)
            throw new InvalidOperationException("Invalid key count.");
        var phones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in keys)
        {
            if (item.Type is not ("Магніт" or "Перепустка") || !PhoneNumber.TryNormalize(item.Phone, out var phone) ||
                !phones.Add(phone!))
            {
                throw new InvalidOperationException("Invalid key order item.");
            }
        }
        var matching = await ApprovedPhones(db).Where(row => phones.Contains(row.Phone)).Select(row => row.Phone).Distinct().CountAsync(ct);
        if (matching != phones.Count)
            throw new InvalidOperationException("A resident phone in the key order is no longer available.");
    }

    private async Task DeleteRequestAsync(DataContext db, CommandEnvelope command, RequestDeletion deletion, CancellationToken ct)
    {
        CheckOwner(command, deletion.OwnerId, deletion.ComplexId);
        await RequireMembershipAsync(db, command.UserId, ct);
        var row = await FindRequestAsync(db, command.UserId, deletion.Id, ct);
        if (row is not { Status: RequestStatus.New } || row.Version != deletion.ExpectedVersion)
            throw new InvalidOperationException("Request changed before deletion.");
        db.Requests.Remove(row);
        var now = clock.GetUtcNow();
        var audit = CreateRequestEvent(deletion.Id, "EVENT#DELETED", now);
        audit.ActorId = command.UserId;
        audit.Action = "Deleted";
        audit.PreviousStatus = "New";
        db.RequestEvents.Add(audit);
    }

    private async Task AddCommentAsync(DataContext db, CommandEnvelope command, ResidentComment comment, CancellationToken ct)
    {
        CheckOwner(command, comment.OwnerId, comment.ComplexId);
        await RequireMembershipAsync(db, command.UserId, ct);
        if (string.IsNullOrWhiteSpace(comment.Text) || comment.Text.Length > 1000 ||
            await FindRequestAsync(db, command.UserId, comment.RequestId, ct) is null)
        {
            throw new InvalidOperationException("Invalid resident comment.");
        }

        var audit = CreateRequestEvent(comment.RequestId, FormattableString.Invariant($"EVENT#UPDATE#{command.UpdateId}"), comment.CreatedAt);
        audit.ActorId = command.UserId;
        audit.Action = "ResidentComment";
        audit.Resolution = comment.Text;
        audit.HasResolution = true;
        db.RequestEvents.Add(audit);
    }

    private RequestEvent CreateRequestEvent(long requestId, string eventKey, DateTimeOffset time) =>
        new()
        {
            SourceId = sourceId,
            Key = FormattableString.Invariant($"LOCAL#{complexId}#{requestId}#{eventKey}"),
            RequestId = requestId,
            EventKey = eventKey,
            CreatedAt = time
        };

    private IQueryable<Request> Requests(DataContext db) => db.Requests.Where(row => row.SourceId == sourceId && row.ComplexId == complexId);

    private Task<Request?> FindRequestAsync(DataContext db, long ownerId, long requestId, CancellationToken ct) =>
        Requests(db).SingleOrDefaultAsync(row => row.OwnerId == ownerId && row.RequestId == requestId, ct);

    private async Task<MaintenanceRequest[]> ToRequestsAsync(DataContext db, IReadOnlyList<Request> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(row => row.RequestId).ToArray();
        var ownerId = rows[0].OwnerId;
        var keys = await db.RequestKeyItems.Where(key => key.SourceId == sourceId && key.OwnerId == ownerId && ids.Contains(key.RequestId))
            .OrderBy(key => key.Position).ToListAsync(ct);
        var byRequest = keys.ToLookup(key => key.RequestId);
        return rows.Select(row =>
        {
            var items = byRequest[row.RequestId].Select(key => new KeyOrderItem(key.Type, key.Phone, key.SerialNumber)).ToArray();
            return new MaintenanceRequest(row.RequestId, row.OwnerId, complex, row.Category ?? "", row.Location ?? "", row.Description ?? "",
                row.CreatedAt, row.Status, row.Visibility, row.Resolution, row.AssignedTo, row.Priority, row.PlannedDate, row.Scope,
                row.BuildingNumber, row.Floor, row.Entrance, row.KeyType, row.KeyQuantity, items.Length == 0 ? null : items);
        }).ToArray();
    }
}
