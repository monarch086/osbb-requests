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
    public async Task<Page<PendingResident>> List(CancellationToken ct) =>
        await residents.ListRegistrationsAsync(Actor, Query("cursor"), ct);

    [HttpGet("count")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> Count(CancellationToken ct) =>
        Ok(new { count = await residents.CountRegistrationsAsync(Actor, ct) });

    [HttpGet("directory")]
    [RequireAdminAccess(AdminPermission.PendingDirectory)]
    public async Task<IActionResult> Directory(CancellationToken ct) =>
        Ok(await residents.ListResidentDirectoryAsync(Actor, true, DirectoryFilters(),
            Query("page"), Query("cursor"), false, ct));

    [HttpGet("export")]
    [RequireAdminAccess(AdminPermission.PendingDirectory)]
    public async Task<FileContentResult> Export(CancellationToken ct) =>
        Workbook(await residents.ExportResidentsAsync(Actor, true, DirectoryFilters(), ct));

    [HttpGet("{reference}/approval")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<IActionResult> Approval(string reference, CancellationToken ct) =>
        new JsonResult(await residents.GetRegistrationApprovalAsync(Actor, reference, ct));

    [HttpGet("{reference}/history")]
    [RequireAdminAccess(AdminPermission.RegistrationHistory)]
    public async Task<Page<ResidentTypeChange>> History(string reference, CancellationToken ct) =>
        await residents.GetRegistrationHistoryAsync(Actor, reference, Query("cursor"), ct);

    [HttpPost("{reference}/decision")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> Decide(string reference, CancellationToken ct)
    {
        ResidentService.ValidateRegistrationReference(reference);
        await residents.DecideRegistrationAsync(Actor, reference, await ReadBodyAsync<RegistrationDecision>(ct), ct);
        return Saved();
    }

    [HttpPatch("{reference}/data")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> Update(string reference, CancellationToken ct)
    {
        ResidentService.ValidateRegistrationReference(reference);
        await residents.UpdateResidentDataAsync(Actor, reference, await ReadBodyAsync<ResidentDataChangeRequest>(ct), ct);
        return Saved();
    }

    [HttpPatch("{reference}/type")]
    [RequireAdminAccess(AdminPermission.ReviewRegistrations)]
    public async Task<IActionResult> ChangeType(string reference, CancellationToken ct)
    {
        ResidentService.ValidateRegistrationReference(reference);
        await residents.ChangeResidentTypeAsync(Actor, reference, await ReadBodyAsync<ResidentTypeChangeRequest>(ct), ct);
        return Saved();
    }
}
