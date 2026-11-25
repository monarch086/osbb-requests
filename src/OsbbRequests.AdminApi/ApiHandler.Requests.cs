using System.Globalization;
using Amazon.Lambda.APIGatewayEvents;
using OsbbRequests.Domain.Admin;

namespace OsbbRequests.AdminApi;

public sealed partial class ApiHandler
{
    private async Task<APIGatewayHttpApiV2ProxyResponse> HandleRequestsAsync(APIGatewayHttpApiV2ProxyRequest request, AdminUser actor,
        string? method, string path, string[] segments, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        if (method == "GET" && path is "/admin/requests" or "/admin/requests/export")
        {
            var search = new RequestSearch(query.GetValueOrDefault("status"), query.GetValueOrDefault("category"),
                query.GetValueOrDefault("search"), query.GetValueOrDefault("from"), query.GetValueOrDefault("to"),
                query.GetValueOrDefault("target"), query.GetValueOrDefault("floor"), query.GetValueOrDefault("section"));
            if (path == "/admin/requests/export")
            {
                RequireDedicatedExportRoute(request, path);
                return Workbook(await service.ExportRequestsAsync(actor, search, ct));
            }
            return Reply(200, await service.ListRequestsAsync(actor, search, query.GetValueOrDefault("cursor"),
                query.GetValueOrDefault("page"), query.GetValueOrDefault("includeCounts"), ct));
        }
        if (segments.Length is 4 or 5 &&
            long.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var owner) && owner > 0 &&
            long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id >= 0)
        {
            var current = await service.GetManagedRequestAsync(actor, owner, id, ct);
            if (method == "GET" && segments.Length == 5 && segments[4] == "history")
                return Reply(200, new { items = await service.GetRequestHistoryAsync(actor, current, ct) });
            if (method == "GET" && segments.Length == 4)
                return Reply(200, await service.GetRequestDetailsAsync(actor, current, ct));
            if (method == "PATCH" && segments.Length == 4)
            {
                await service.UpdateRequestAsync(actor, current, Body<RequestChange>(request), ct);
                return Ok();
            }
        }
        throw RouteNotFound();
    }
}
