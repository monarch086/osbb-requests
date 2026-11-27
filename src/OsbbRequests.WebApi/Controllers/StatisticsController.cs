using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/statistics")]
[RequireAdminAccess]
public sealed class StatisticsController(StatisticsService statistics) : ControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.Statistics)]
    public async Task<StatisticsReport> Get([FromQuery] string? days, CancellationToken ct) =>
        await statistics.GetStatisticsAsync(HttpContext.GetAdminActor(), days, ct);
}
