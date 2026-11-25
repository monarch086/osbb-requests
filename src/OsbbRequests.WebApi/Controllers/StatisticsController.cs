using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/statistics")]
public sealed class StatisticsController(StatisticsService statistics) : AdminControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.Statistics)]
    public async Task<StatisticsReport> Get(CancellationToken ct) =>
        await statistics.GetStatisticsAsync(Actor, Query("days"), ct);
}
