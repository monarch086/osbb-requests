using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;

namespace OsbbRequests.AdminApi;

public sealed partial class ApiHandler(PermissionService permissions, ResidentService residents, RequestService requests,
    MessageService messages, StatisticsService statistics)
{
    private const int MaxBodyLength = 10000;
    private static readonly IReadOnlyDictionary<string, string> EmptyQuery = new Dictionary<string, string>();

    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(APIGatewayHttpApiV2ProxyRequest request, CancellationToken ct)
    {
        try { return await DispatchAsync(request, ct); }
        catch (AdminOperationException error)
        {
            throw new ApiError(error.Failure switch
            {
                AdminFailure.InvalidInput => 400,
                AdminFailure.Unauthenticated => 401,
                AdminFailure.Forbidden => 403,
                AdminFailure.NotFound => 404,
                AdminFailure.Conflict => 409,
                AdminFailure.TooLarge => 413,
                AdminFailure.Unavailable => 503,
                _ => throw new ArgumentOutOfRangeException(nameof(error), error.Failure, "Unknown administrative failure.")
            }, error.Message, error);
        }
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> DispatchAsync(APIGatewayHttpApiV2ProxyRequest request, CancellationToken ct)
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

        if (method == "GET" && path == "/admin/me")
        {
            var authorization = request.Headers?.FirstOrDefault(p => p.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)).Value;
            var token = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? authorization[7..] : null;
            return Reply(200, await permissions.GetOrRegisterAdminAsync(userId, token, ct));
        }

        var actor = await permissions.GetActiveAdminAsync(userId, ct);
        var query = request.QueryStringParameters?.ToDictionary(p => p.Key, p => p.Value) ?? EmptyQuery;
        var cursor = query.GetValueOrDefault("cursor");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (method == "GET" && path == "/admin/statistics")
            return Reply(200, await statistics.GetStatisticsAsync(actor, query.GetValueOrDefault("days"), ct));
        if (path == "/admin/messages")
        {
            MessageService.RequireMessagesAccess(actor);
            if (method == "GET") return Reply(200, await messages.ListMessagesAsync(actor, cursor, ct));
            if (method == "POST") return Reply(200, await messages.SendMessageAsync(actor, Body<MessageChange>(request), ct));
        }
        if (method == "PATCH" && segments is ["admin", "administrators", var permissionTarget, "permissions"])
        {
            PermissionService.RequirePermissionsEdit(actor);
            await permissions.SetPermissionsAsync(actor, permissionTarget, Body<PermissionsChange>(request), ct);
            return Ok();
        }
        if (method == "PATCH" && segments is ["admin", "administrators", var targetId, "notifications"])
        {
            PermissionService.RequireNotificationEdit(actor, targetId);
            await permissions.SetNotificationSettingsAsync(actor, targetId, Body<NotificationSettingsChange>(request), ct);
            return Ok();
        }
        if (path == "/admin/administrators" || segments is ["admin", "administrators", _])
        {
            PermissionService.RequireAdministratorManagement(actor);
            if (method == "GET" && path == "/admin/administrators") return Reply(200, await permissions.ListAdminsAsync(actor, cursor, ct));
            if (method == "PATCH" && segments.Length == 3)
            {
                await permissions.SetAccessAsync(actor, segments[2], Body<AccessChange>(request), ct);
                return Ok();
            }
        }
        if (method == "GET" && path == "/admin/assignees")
            return Reply(200, await requests.ListAssigneesAsync(actor, cursor, ct));
        if (segments is ["admin", "residents" or "registrations" or "apartment-reviews", ..])
            return await HandleResidentsAsync(request, actor, method, path, segments, query, ct);
        if (segments is ["admin", "requests", ..])
            return await HandleRequestsAsync(request, actor, method, path, segments, query, ct);
        throw RouteNotFound();
    }

    private static T Body<T>(APIGatewayHttpApiV2ProxyRequest request)
    {
        try
        {
            if (request.Body is not { Length: <= MaxBodyLength } body) throw new JsonException();
            if (request.IsBase64Encoded) body = Encoding.UTF8.GetString(Convert.FromBase64String(body));
            return JsonSerializer.Deserialize<T>(body, AdminJson.Options) ?? throw new JsonException();
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new ApiError(400, "Некоректні дані запиту.");
        }
    }

    private static void RequireDedicatedExportRoute(APIGatewayHttpApiV2ProxyRequest request, string path)
    {
        // The greedy proxy must not bypass the per-export route throttle.
        if (request.RawPath != path || request.RouteKey != "GET " + path) throw RouteNotFound();
    }

    private static ApiError RouteNotFound() => new(404, "Сторінку не знайдено.");

    private static APIGatewayHttpApiV2ProxyResponse Ok() => Reply(200, new { ok = true });

    public static APIGatewayHttpApiV2ProxyResponse Reply(int status, object? body) => new()
    {
        StatusCode = status,
        Body = JsonSerializer.Serialize(body, AdminJson.Options),
        Headers = new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8", ["cache-control"] = "no-store" }
    };

    private static APIGatewayHttpApiV2ProxyResponse Workbook(byte[] file) => new()
    {
        StatusCode = 200,
        Body = Convert.ToBase64String(file),
        IsBase64Encoded = true,
        Headers = new Dictionary<string, string> { ["content-type"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ["cache-control"] = "no-store" }
    };
}
