using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/registrations")]
public sealed class RegistrationsController(ResidentService residents) : AdminControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<Page<PendingResident>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await residents.ListRegistrationsAsync(Actor, cursor, ct);

    [HttpGet("count")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> Count(CancellationToken ct) =>
        Ok(new { count = await residents.CountRegistrationsAsync(Actor, ct) });

    [HttpGet("directory")]
    [RequireAdminAccess(AdminPermission.PendingDirectory)]
    public async Task<IActionResult> Directory([FromQuery] ResidentDirectoryFilters filters, [FromQuery] string? page,
        [FromQuery] string? cursor, CancellationToken ct) =>
        Ok(await residents.ListResidentDirectoryAsync(Actor, true, filters,
            page, cursor, false, ct));

    [HttpGet("export")]
    [RequireAdminAccess(AdminPermission.PendingDirectory)]
    public async Task<FileContentResult> Export([FromQuery] ResidentDirectoryFilters filters, CancellationToken ct) =>
        Workbook(await residents.ExportResidentsAsync(Actor, true, filters, ct));

    [HttpGet("{reference}/approval")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<IActionResult> Approval([FromRoute] string reference, CancellationToken ct) =>
        new JsonResult(await residents.GetRegistrationApprovalAsync(Actor, reference, ct));

    [HttpGet("{reference}/history")]
    [RequireAdminAccess(AdminPermission.RegistrationHistory)]
    public async Task<Page<ResidentTypeChange>> History([FromRoute] string reference, [FromQuery] string? cursor, CancellationToken ct) =>
        await residents.GetRegistrationHistoryAsync(Actor, reference, cursor, ct);

    [HttpPost("{reference}/decision")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> Decide([FromRoute] string reference, [FromBody] RegistrationDecision change, CancellationToken ct)
    {
        await residents.DecideRegistrationAsync(Actor, reference, change, ct);
        return Saved();
    }

    [HttpPatch("{reference}/data")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> Update([FromRoute] string reference, [FromBody] ResidentDataChangeRequest change, CancellationToken ct)
    {
        await residents.UpdateResidentDataAsync(Actor, reference, change, ct);
        return Saved();
    }

    [HttpPatch("{reference}/type")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> ChangeType([FromRoute] string reference, [FromBody] ResidentTypeChangeRequest change, CancellationToken ct)
    {
        await residents.ChangeResidentTypeAsync(Actor, reference, change, ct);
        return Saved();
    }
}
