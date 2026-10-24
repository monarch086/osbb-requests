using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.AdminApi.Auth;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
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
            var page = await store.ListAdminsAsync(cursor, ct);
            return Reply(200, new { items = page.Items.Where(a => a.IsActive).Select(a => new { a.UserId, a.Email }), page.Cursor });
        }
        if (method == "GET" && path == "/admin/residents") return Reply(200, await store.ListResidentsAsync(cursor, ct));
        if (method == "GET" && path == "/admin/registrations")
        {
            var page = await store.ListRegistrationsAsync(cursor, ct);
            return Reply(200, new
            {
                items = page.Items.Select(r => new
                {
                    r.Id,
                    r.FirstName,
                    r.LastName,
                    r.Phone,
                    r.UpdatedAt,
                    r.Version,
                    TelegramChatId = r.UserId.ToString(CultureInfo.InvariantCulture)
                }),
                page.Cursor
            });
        }
        if (method == "POST" && segments is ["admin", "registrations", var reference, "decision"])
        {
            if (reference.Length != 32 || !reference.All(Uri.IsHexDigit)) throw new ApiError(400, "Некоректний запит.");
            await store.DecideAsync(actor, reference, Body<RegistrationDecision>(request), ct);
            return Reply(200, new { ok = true });
        }
        if (method == "GET" && path == "/admin/requests")
        {
            var status = query.GetValueOrDefault("status");
            var statuses = string.IsNullOrEmpty(status) ? [] : status.Split(',').Distinct(StringComparer.Ordinal).ToArray();
            if (status?.Length > 150 || statuses.Any(value => !Enum.GetNames<RequestStatus>().Contains(value, StringComparer.Ordinal)))
                throw new ApiError(400, "Некоректний статус.");
            var from = Date(query.GetValueOrDefault("from")); var to = Date(query.GetValueOrDefault("to"));
            if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0) throw new ApiError(400, "Некоректний період.");
            if (query.GetValueOrDefault("search")?.Length > 200) throw new ApiError(400, "Пошук: до 200 символів.");
            var target = query.GetValueOrDefault("target");
            if (target is { Length: > 0 } && target is not ("Complex" or "Unspecified") && !ComplexLayout.BuildingNumbers.Any(number => target == "Building" + number))
                throw new ApiError(400, "Оберіть будинок 4Б, 4В, 4Г або 4Д або весь комплекс.");
            int? floor = null;
            if (query.GetValueOrDefault("floor") is { Length: > 0 } floorText)
            {
                if (!int.TryParse(floorText, NumberStyles.None, CultureInfo.InvariantCulture, out var floorNumber) || floorNumber is < ComplexLayout.EntranceFloor or > ComplexLayout.FloorCount)
                    throw new ApiError(400, "Оберіть «Парадне» або поверх від 1 до 25.");
                floor = floorNumber;
            }
            return Reply(200, await store.ListRequestsAsync(new(statuses, query.GetValueOrDefault("category"),
                query.GetValueOrDefault("search"), from, to, target, floor), cursor, ct));
        }
        if (segments.Length is 4 or 5 && segments[0] == "admin" && segments[1] == "requests" &&
            long.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var owner) && owner > 0 &&
            long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0)
        {
            var current = await store.GetRequestAsync(owner, id, ct) ?? throw new ApiError(404, "Заявку не знайдено.");
            if (method == "GET" && segments.Length == 5 && segments[4] == "history") return Reply(200, new { items = await store.HistoryAsync(current.Request, ct) });
            if (method == "GET" && segments.Length == 4) return Reply(200, current);
            if (method == "PATCH" && segments.Length == 4)
            {
                var change = Body<RequestChange>(request);
                if (change.Version != current.Version) throw new ApiError(409, "Заявку змінено. Оновіть список.");
                if (!Enum.TryParse<RequestStatus>(change.Status, out var status) || !Enum.IsDefined(status) ||
                    !Enum.TryParse<RequestVisibility>(change.Visibility, out var visibility) || !Enum.IsDefined(visibility) ||
                    change.Priority is not ("Low" or "Normal" or "High" or "Urgent")) throw new ApiError(400, "Некоректні параметри заявки.");
                if (change.Resolution?.Length > 1000 || status is RequestStatus.Completed or RequestStatus.Rejected && string.IsNullOrWhiteSpace(change.Resolution))
                    throw new ApiError(400, "Для виконання або відхилення потрібне пояснення до 1000 символів.");
                if (!Enum.TryParse<RequestScope>(change.Scope, out var scope) || !ComplexLayout.IsValid(scope, change.BuildingNumber, change.Floor, change.Entrance))
                    throw new ApiError(400, "Оберіть будинок 4Б, 4В, 4Г або 4Д або весь комплекс. Для будинку оберіть поверх 1–25 або «Парадне»; під’їзд — 1, 2 або «Не зазначено» (для 4Б лише 1 або «Не зазначено»).");
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
    public static APIGatewayHttpApiV2ProxyResponse Reply(int status, object body) => new()
    {
        StatusCode = status,
        Body = JsonSerializer.Serialize(body, Json),
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8", ["cache-control"] = "no-store" }
    };
}
