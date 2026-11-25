using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin")]
public sealed class RequestsController(RequestService requests) : AdminControllerBase
{
    [HttpGet("assignees")]
    [RequireAdminAccess(AdminPermission.Assignees)]
    public async Task<Page<Assignee>> Assignees(CancellationToken ct) =>
        await requests.ListAssigneesAsync(Actor, Query("cursor"), ct);

    [HttpGet("requests")]
    public async Task<AdminRequestPage> List(CancellationToken ct) =>
        await requests.ListRequestsAsync(Actor, Search(), Query("cursor"), Query("page"), Query("includeCounts"), ct);

    [HttpGet("requests/export")]
    public async Task<FileContentResult> Export(CancellationToken ct) =>
        Workbook(await requests.ExportRequestsAsync(Actor, Search(), ct));

    [HttpGet("requests/{owner}/{id}")]
    public async Task<ManagedRequest> Details(string owner, string id, CancellationToken ct)
    {
        var actor = Actor;
        return await requests.GetRequestDetailsAsync(actor, await GetRequestAsync(actor, owner, id, ct), ct);
    }

    [HttpGet("requests/{owner}/{id}/history")]
    public async Task<IActionResult> History(string owner, string id, CancellationToken ct)
    {
        var actor = Actor;
        return Ok(new { items = await requests.GetRequestHistoryAsync(actor, await GetRequestAsync(actor, owner, id, ct), ct) });
    }

    [HttpPatch("requests/{owner}/{id}")]
    public async Task<IActionResult> Update(string owner, string id, CancellationToken ct)
    {
        var actor = Actor;
        var current = await GetRequestAsync(actor, owner, id, ct);
        await requests.UpdateRequestAsync(actor, current, await ReadBodyAsync<RequestChange>(ct), ct);
        return Saved();
    }

    private RequestSearch Search() => new(Query("status"), Query("category"), Query("search"), Query("from"), Query("to"),
        Query("target"), Query("floor"), Query("section"));

    private Task<ManagedRequest> GetRequestAsync(AdminUser actor, string owner, string id, CancellationToken ct)
    {
        if (!long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) || ownerId <= 0 ||
            !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId))
            throw new AdminOperationException(AdminFailure.NotFound, "Сторінку не знайдено.");
        return requests.GetManagedRequestAsync(actor, ownerId, requestId, ct);
    }
}
