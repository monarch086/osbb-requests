using OsbbRequests.Shared.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.AdminApi.Auth;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.AdminApi.Export;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.AdminApi;

public sealed class ApiHandler(IAdminStore store, IIdentityProvider identityProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };

    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(APIGatewayHttpApiV2ProxyRequest request, CancellationToken ct)
    {
        var method = request.RequestContext?.Http?.Method;
        var path = request.RawPath?.TrimEnd('/') ?? "";
        if (method == "OPTIONS" && path.StartsWith("/admin/", StringComparison.Ordinal))
            return new APIGatewayHttpApiV2ProxyResponse { StatusCode = 204 };
        if (method == "GET" && path == "/health") return Reply(200, new { status = "ok" });
        var claims = request.RequestContext?.Authorizer?.Jwt?.Claims?.ToDictionary(p => p.Key, p => p.Value);
        if (claims is null || claims.GetValueOrDefault("token_use") != "access" ||
            !claims.TryGetValue("sub", out var userId) || string.IsNullOrWhiteSpace(userId))
            throw new ApiError(401, "Потрібно увійти до облікового запису.");
        var actor = await store.GetAdminAsync(userId, ct);
        if (method == "GET" && path == "/admin/me")
        {
            if (actor is null)
            {
                var token = request.Headers?.FirstOrDefault(p => p.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
                if (token is null || !token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new ApiError(401, "Потрібно увійти.");
                var identity = await identityProvider.GetAsync(token[7..], userId, ct);
                actor = await store.RegisterAccountAsync(identity, ct);
            }
            return Reply(200, actor);
        }
        if (actor?.IsActive != true) throw new ApiError(403, "Адміністративний доступ ще не надано.");
        var query = request.QueryStringParameters?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string>();
        var cursor = query.GetValueOrDefault("cursor");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path == "/admin/messages")
        {
            if (!actor.EffectivePermissions.Messages) throw new ApiError(403, "Недостатньо прав для роботи з повідомленнями.");
            if (method == "GET") return Reply(200, await store.ListMessagesAsync(cursor, ct));
            if (method == "POST") return Reply(200, await store.SendMessageAsync(actor, Body<MessageChange>(request), ct));
        }
        if (method == "PATCH" && segments is ["admin", "administrators", var permissionTarget, "permissions"])
        {
            if (!actor.IsSuperAdmin) throw new ApiError(403, "Недостатньо прав для зміни доступів.");
            var change = Body<PermissionsChange>(request);
            var target = await store.GetAdminAsync(permissionTarget, ct) ?? throw new ApiError(404, "Користувача не знайдено.");
            if (target.IsSuperAdmin || target.UserId == actor.UserId) throw new ApiError(403, "Цей обліковий запис керується через AWS.");
            if (change.Version != target.Version) throw new ApiError(409, "Профіль змінено. Оновіть список.");
            await store.SetPermissionsAsync(actor, target, change, ct);
            return Reply(200, new { ok = true });
        }
        if (method == "PATCH" && segments is ["admin", "administrators", var targetId, "notifications"])
        {
            if (actor.UserId != targetId && !actor.IsSuperAdmin) throw new ApiError(403, "Недостатньо прав для зміни сповіщень.");
            var change = Body<NotificationSettingsChange>(request);
            var target = await store.GetAdminAsync(targetId, ct) ?? throw new ApiError(404, "Користувача не знайдено.");
            if (change.Version != target.Version) throw new ApiError(409, "Профіль змінено. Оновіть список.");
            await store.SetNotificationSettingsAsync(actor, target, change, ct);
            return Reply(200, new { ok = true });
        }
        if (path == "/admin/administrators" || segments is ["admin", "administrators", _])
        {
            if (!actor.IsSuperAdmin) throw new ApiError(403, "Керувати адміністраторами може лише супер адміністратор.");
            if (method == "GET" && path == "/admin/administrators") return Reply(200, await store.ListAdminsAsync(cursor, ct));
            if (method == "PATCH" && segments.Length == 3)
            {
                var change = Body<AccessChange>(request);
                var target = await store.GetAdminAsync(segments[2], ct) ?? throw new ApiError(404, "Користувача не знайдено.");
                if (target.IsSuperAdmin || target.UserId == actor.UserId) throw new ApiError(403, "Цей обліковий запис керується через AWS.");
                if (change.Version != target.Version) throw new ApiError(409, "Профіль змінено. Оновіть список.");
                await store.SetAccessAsync(actor, target, change, ct);
                return Reply(200, new { ok = true });
            }
        }
        if (method == "GET" && path == "/admin/assignees")
        {
            if (!actor.EffectivePermissions.ComplexRequests && !actor.EffectivePermissions.KeyOrders)
                throw new ApiError(403, "Недостатньо прав для роботи із заявками.");
            var page = await store.ListAdminsAsync(cursor, ct);
            return Reply(200, new { items = page.Items.Where(a => a.IsActive).Select(a => new { a.UserId, a.Email }), page.Cursor });
        }
        if (method == "GET" && (path == "/admin/residents/directory" || path == "/admin/registrations/directory"))
        {
            var pending = path == "/admin/registrations/directory";
            if (pending ? !actor.EffectivePermissions.PendingResidents : !actor.EffectivePermissions.RegisteredResidents)
                throw new ApiError(403, "Недостатньо прав для перегляду мешканців.");
            if (!int.TryParse(query.GetValueOrDefault("page") ?? "1", out var number)) throw new ApiError(400, "Некоректна сторінка.");
            var directory = await store.ListResidentDirectoryAsync(pending, number, query.GetValueOrDefault("building"),
                query.GetValueOrDefault("residentType"), query.GetValueOrDefault("sort"), query.GetValueOrDefault("direction"), ct);
            if (pending) return Reply(200, new
            {
                items = directory.Items.Select(r => new
                {
                    r.Id,
                    r.FirstName,
                    r.LastName,
                    r.Phone,
                    r.Email,
                    r.BuildingNumber,
                    r.ApartmentNumber,
                    r.ResidentType,
                    r.TypeChangeCount,
                    r.ConsentAt,
                    r.UpdatedAt,
                    r.Version,
                    TelegramChatId = r.UserId.ToString(CultureInfo.InvariantCulture)
                }),
                directory.PageNumber,
                directory.PageSize,
                directory.Total
            });
            return Reply(200, new
            {
                items = directory.Items.Select(r => new RegisteredResident(r.Id, r.FirstName, r.LastName, r.Phone,
                    r.ApprovedAt ?? r.UpdatedAt, r.UserId.ToString(CultureInfo.InvariantCulture), r.BuildingNumber,
                    r.ApartmentNumber, r.PersonalDataDeleted, r.Email, r.ConsentAt, r.ResidentType, r.TypeChangeCount, r.Version)),
                directory.PageNumber,
                directory.PageSize,
                directory.Total
            });
        }
        if (method == "GET" && path == "/admin/residents")
        {
            if (!actor.EffectivePermissions.RegisteredResidents) throw new ApiError(403, "Недостатньо прав для перегляду мешканців.");
            return Reply(200, await store.ListResidentsAsync(cursor, ct));
        }
        if (method == "GET" && path == "/admin/registrations/count")
        {
            if (!actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
            return Reply(200, new { count = await store.CountRegistrationsAsync(ct) });
        }
        if (method == "GET" && path == "/admin/registrations")
        {
            if (!actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
            var page = await store.ListRegistrationsAsync(cursor, ct);
            return Reply(200, new
            {
                items = page.Items.Select(r => new
                {
                    r.Id,
                    r.FirstName,
                    r.LastName,
                    r.Phone,
                    r.Email,
                    r.BuildingNumber,
                    r.ApartmentNumber,
                    r.ResidentType,
                    r.TypeChangeCount,
                    r.ConsentAt,
                    r.UpdatedAt,
                    r.Version,
                    TelegramChatId = r.UserId.ToString(CultureInfo.InvariantCulture)
                }),
                page.Cursor
            });
        }
        if (segments is ["admin", "registrations", var residentId, "data"] && method == "PATCH")
        {
            if (!actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
            if (residentId.Length != 32 || !residentId.All(Uri.IsHexDigit)) throw new ApiError(400, "Некоректний запит.");
            await store.UpdateResidentDataAsync(actor, residentId, Body<ResidentDataChangeRequest>(request), ct);
            return Reply(200, new { ok = true });
        }
        if (segments is ["admin", "registrations", var registrationId, "type"] && method == "PATCH")
        {
            if (!actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
            if (registrationId.Length != 32 || !registrationId.All(Uri.IsHexDigit)) throw new ApiError(400, "Некоректний запит.");
            await store.ChangeResidentTypeAsync(actor, registrationId, Body<ResidentTypeChangeRequest>(request), ct);
            return Reply(200, new { ok = true });
        }
        if (segments is ["admin", "registrations", var approvalId, "approval"] && method == "GET")
        {
            if (!actor.EffectivePermissions.RegisteredResidents) throw new ApiError(403, "Недостатньо прав для перегляду мешканців.");
            if (approvalId.Length != 32 || !approvalId.All(Uri.IsHexDigit)) throw new ApiError(400, "Некоректний запит.");
            var registration = await store.GetRegistrationAsync(approvalId, ct) ?? throw new ApiError(404, "Реєстрацію не знайдено.");
            if (registration.Stage != RegistrationStage.Approved || registration.PersonalDataDeleted) throw new ApiError(404, "Реєстрацію не знайдено.");
            return Reply(200, await store.GetRegistrationApprovalAsync(approvalId, ct));
        }
        if (segments is ["admin", "registrations", var historyId, "history"] && method == "GET")
        {
            if (!actor.EffectivePermissions.PendingResidents && !actor.EffectivePermissions.RegisteredResidents)
                throw new ApiError(403, "Недостатньо прав для перегляду історії.");
            if (historyId.Length != 32 || !historyId.All(Uri.IsHexDigit)) throw new ApiError(400, "Некоректний запит.");
            var registration = await store.GetRegistrationAsync(historyId, ct) ?? throw new ApiError(404, "Реєстрацію не знайдено.");
            if (registration.Stage == RegistrationStage.Pending ? !actor.EffectivePermissions.PendingResidents :
                registration.Stage == RegistrationStage.Approved ? !actor.EffectivePermissions.RegisteredResidents : true)
                throw new ApiError(403, "Недостатньо прав для перегляду історії.");
            return Reply(200, await store.RegistrationHistoryAsync(historyId, cursor, ct));
        }
        if (method == "POST" && segments is ["admin", "registrations", var reference, "decision"])
        {
            if (!actor.EffectivePermissions.PendingResidents) throw new ApiError(403, "Недостатньо прав для перевірки реєстрацій.");
            if (reference.Length != 32 || !reference.All(Uri.IsHexDigit)) throw new ApiError(400, "Некоректний запит.");
            await store.DecideAsync(actor, reference, Body<RegistrationDecision>(request), ct);
            return Reply(200, new { ok = true });
        }
        if (method == "GET" && path is "/admin/requests" or "/admin/requests/export")
        {
            // The export must use the explicitly throttled API Gateway route, not the greedy proxy.
            if (path == "/admin/requests/export" && (request.RawPath != path || request.RouteKey != "GET /admin/requests/export")) throw new ApiError(404, "Сторінку не знайдено.");
            var status = query.GetValueOrDefault("status");
            var statuses = string.IsNullOrEmpty(status) ? [] : status.Split(',').Distinct(StringComparer.Ordinal).ToArray();
            if (status?.Length > 150 || statuses.Any(value => !Enum.GetNames<RequestStatus>().Contains(value, StringComparer.Ordinal)))
                throw new ApiError(400, "Некоректний статус.");
            var from = Date(query.GetValueOrDefault("from")); var to = Date(query.GetValueOrDefault("to"));
            if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0) throw new ApiError(400, "Некоректний період.");
            if (query.GetValueOrDefault("search")?.Length > 200) throw new ApiError(400, "Пошук: до 200 символів.");
            var target = query.GetValueOrDefault("target");
            if (target is { Length: > 0 } && target is not ("Complex" or "Unspecified") && !ComplexLayout.BuildingNumbers.Any(number => target == "Building" + number))
                throw new ApiError(400, "Оберіть будинок 4Б, 4В, 4Г або 4Д або прибудинкову територію.");
            int? floor = null;
            if (query.GetValueOrDefault("floor") is { Length: > 0 } floorText)
            {
                if (!int.TryParse(floorText, NumberStyles.None, CultureInfo.InvariantCulture, out var floorNumber) || floorNumber is < ComplexLayout.EntranceFloor or > ComplexLayout.FloorCount)
                    throw new ApiError(400, "Оберіть «Парадне» або поверх від 1 до 25.");
                floor = floorNumber;
            }
            var section = query.GetValueOrDefault("section");
            if (section is not (null or "complex" or "keys")) throw new ApiError(400, "Некоректний розділ заявок.");
            if (section == "complex" && !actor.EffectivePermissions.ComplexRequests || section == "keys" && !actor.EffectivePermissions.KeyOrders ||
                section is null && !(actor.EffectivePermissions.ComplexRequests && actor.EffectivePermissions.KeyOrders))
                throw new ApiError(403, "Недостатньо прав для перегляду заявок.");
            var filters = new RequestFilters(statuses, query.GetValueOrDefault("category"),
                query.GetValueOrDefault("search"), from, to, target, floor, section);
            if (path == "/admin/requests/export")
            {
                var file = await new TicketExport(store).CreateAsync(filters, ct);
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = 200,
                    IsBase64Encoded = true,
                    Body = Convert.ToBase64String(file),
                    Headers = new Dictionary<string, string> { ["content-type"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ["cache-control"] = "no-store" }
                };
            }
            return Reply(200, await store.ListRequestsAsync(filters, cursor, ct));
        }
        if (segments.Length is 4 or 5 && segments[0] == "admin" && segments[1] == "requests" &&
            long.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var owner) && owner > 0 &&
            long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0)
        {
            var current = await store.GetRequestAsync(owner, id, ct) ?? throw new ApiError(404, "Заявку не знайдено.");
            if (current.Request.Category == RequestCategories.KeyOrder ? !actor.EffectivePermissions.KeyOrders : !actor.EffectivePermissions.ComplexRequests)
                throw new ApiError(403, "Недостатньо прав для роботи із заявкою.");
            if (method == "GET" && segments.Length == 5 && segments[4] == "history") return Reply(200, new { items = await store.HistoryAsync(current.Request, ct) });
            if (method == "GET" && segments.Length == 4) return Reply(200, current with { Author = await store.GetRequestAuthorAsync(current.Request, ct) });
            if (method == "PATCH" && segments.Length == 4)
            {
                var change = Body<RequestChange>(request);
                if (change.Version != current.Version) throw new ApiError(409, "Заявку змінено. Оновіть список.");
                if (!Enum.TryParse<RequestStatus>(change.Status, out var status) || !Enum.IsDefined(status) ||
                    !Enum.TryParse<RequestVisibility>(change.Visibility, out var visibility) || !Enum.IsDefined(visibility) ||
                    change.Priority is not ("Low" or "Normal" or "High" or "Urgent")) throw new ApiError(400, "Некоректні параметри заявки.");
                if (current.Request.Category == RequestCategories.KeyOrder && visibility != RequestVisibility.Private)
                    throw new ApiError(400, "Замовлення ключів має бути приватним.");
                if (change.Resolution?.Length > 1000 || status is RequestStatus.Completed or RequestStatus.Rejected && string.IsNullOrWhiteSpace(change.Resolution))
                    throw new ApiError(400, "Для виконання або відхилення потрібне пояснення до 1000 символів.");
                if (!Enum.TryParse<RequestScope>(change.Scope, out var scope) || !ComplexLayout.IsValid(scope, change.BuildingNumber, change.Floor, change.Entrance))
                    throw new ApiError(400, "Оберіть будинок 4Б, 4В, 4Г або 4Д або прибудинкову територію. Для будинку оберіть поверх 1–25 або «Парадне»; під’їзд - 1, 2 або «Не зазначено» (для 4Б лише 1 або «Не зазначено»).");
                var date = Date(change.PlannedDate);
                if (!string.IsNullOrEmpty(change.AssignedTo) && (await store.GetAdminAsync(change.AssignedTo, ct))?.IsActive != true)
                    throw new ApiError(400, "Оберіть активного адміністратора.");
                var next = current.Request with
                {
                    Scope = scope,
                    BuildingNumber = change.BuildingNumber,
                    Floor = change.Floor,
                    Entrance = change.Entrance,
                    Status = status,
                    Visibility = visibility,
                    Resolution = change.Resolution?.Trim(),
                    AssignedTo = string.IsNullOrEmpty(change.AssignedTo) ? null : change.AssignedTo,
                    Priority = change.Priority,
                    PlannedDate = date is null ? null : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
                await store.UpdateRequestAsync(actor, current, next, ct);
                return Reply(200, new { ok = true });
            }
        }
        throw new ApiError(404, "Сторінку не знайдено.");
    }
    private static string? Date(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ApiError(400, "Некоректна дата.");
        return value;
    }
    private static T Body<T>(APIGatewayHttpApiV2ProxyRequest request)
    {
        try
        {
            if (request.Body?.Length > 10000) throw new JsonException();
            var body = request.IsBase64Encoded ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Body ?? "")) : request.Body ?? "";
            return JsonSerializer.Deserialize<T>(body, Json) ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or FormatException) { throw new ApiError(400, "Некоректні дані запиту."); }
    }
    public static APIGatewayHttpApiV2ProxyResponse Reply(int status, object? body) => new()
    {
        StatusCode = status,
        Body = JsonSerializer.Serialize(body, Json),
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8", ["cache-control"] = "no-store" }
    };
}
