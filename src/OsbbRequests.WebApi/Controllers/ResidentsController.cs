using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/residents")]
public sealed class ResidentsController(ResidentService residents) : AdminControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<Page<RegisteredResident>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await residents.ListResidentsAsync(Actor, cursor, ct);

    [HttpGet("directory")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<IActionResult> Directory([FromQuery] ResidentDirectoryFilters filters, [FromQuery] string? page,
        [FromQuery] string? cursor, [FromQuery] string? compact, CancellationToken ct) =>
        Ok(await residents.ListResidentDirectoryAsync(Actor, false, filters,
            page, cursor, compact == "true", ct));

    [HttpGet("export")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<FileContentResult> Export([FromQuery] ResidentDirectoryFilters filters, CancellationToken ct) =>
        Workbook(await residents.ExportResidentsAsync(Actor, false, filters, ct));

    [HttpGet("{reference}")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<RegisteredResidentDetails> Details([FromRoute] string reference, CancellationToken ct) =>
        await residents.GetRegisteredResidentAsync(Actor, reference, ct);

    [HttpGet("keys")]
    [RequireAdminAccess(AdminPermission.ReadKeys)]
    public async Task<ResidentKeyPage> Keys([FromQuery] string? page, CancellationToken ct) =>
        await residents.ListResidentsWithKeysAsync(Actor, page, ct);

    [HttpPatch("keys/{owner}/{request}/{index}")]
    [RequireAdminAccess(AdminPermission.EditKeys)]
    public async Task<IActionResult> UpdateKey([FromRoute] string owner, [FromRoute] string request,
        [FromRoute] string index, [FromBody] ResidentKeyUpdate change, CancellationToken ct)
    {
        if (!long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) || ownerId <= 0 ||
            !long.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId) ||
            !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний ключ.");
        await residents.UpdateResidentKeyAsync(Actor, ownerId, requestId, position, change, ct);
        return Saved();
    }
}
