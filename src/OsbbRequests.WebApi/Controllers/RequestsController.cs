using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin")]
[RequireAdminAccess]
public sealed class RequestsController(RequestService requests) : ControllerBase
{
    [HttpGet("assignees")]
    [RequireAdminAccess(AdminPermission.Assignees)]
    public async Task<Page<Assignee>> Assignees([FromQuery] string? cursor, CancellationToken ct) =>
        await requests.ListAssigneesAsync(HttpContext.GetAdminActor(), cursor, ct);

    [HttpGet("requests")]
    public async Task<AdminRequestPage> List([FromQuery] RequestSearch filters, [FromQuery] string? cursor,
        [FromQuery] string? page, [FromQuery] string? includeCounts, CancellationToken ct) =>
        await requests.ListRequestsAsync(HttpContext.GetAdminActor(), filters, cursor, page, includeCounts, ct);

    [HttpGet("requests/export")]
    public async Task<FileContentResult> Export([FromQuery] RequestSearch filters, CancellationToken ct) =>
        AdminResponses.Workbook(await requests.ExportRequestsAsync(HttpContext.GetAdminActor(), filters, ct));

    [HttpGet("requests/{owner}/{id}")]
    public async Task<ManagedRequest> Details([FromRoute] string owner, [FromRoute] string id, CancellationToken ct)
    {
        var actor = HttpContext.GetAdminActor();
        return await requests.GetRequestDetailsAsync(actor, await GetRequestAsync(actor, owner, id, ct), ct);
    }

    [HttpGet("requests/{owner}/{id}/history")]
    public async Task<IActionResult> History([FromRoute] string owner, [FromRoute] string id, CancellationToken ct)
    {
        var actor = HttpContext.GetAdminActor();
        return Ok(new { items = await requests.GetRequestHistoryAsync(actor, await GetRequestAsync(actor, owner, id, ct), ct) });
    }

    [HttpPatch("requests/{owner}/{id}")]
    public async Task<IActionResult> Update([FromRoute] string owner, [FromRoute] string id, [FromBody] RequestChange change, CancellationToken ct)
    {
        var actor = HttpContext.GetAdminActor();
        var current = await GetRequestAsync(actor, owner, id, ct);
        await requests.UpdateRequestAsync(actor, current, change, ct);
        return AdminResponses.Saved();
    }

    private Task<ManagedRequest> GetRequestAsync(AdminUser actor, string owner, string id, CancellationToken ct)
    {
        if (!long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) || ownerId <= 0 ||
            !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId))
        {
            throw new AdminOperationException(AdminFailure.NotFound, "Сторінку не знайдено.");
        }

        return requests.GetManagedRequestAsync(actor, ownerId, requestId, ct);
    }
}
