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
    public async Task<Page<RegisteredResident>> List(CancellationToken ct) =>
        await residents.ListResidentsAsync(Actor, Query("cursor"), ct);

    [HttpGet("directory")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<IActionResult> Directory(CancellationToken ct) =>
        Ok(await residents.ListResidentDirectoryAsync(Actor, false, DirectoryFilters(),
            Query("page"), Query("cursor"), Query("compact") == "true", ct));

    [HttpGet("export")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<FileContentResult> Export(CancellationToken ct) =>
        Workbook(await residents.ExportResidentsAsync(Actor, false, DirectoryFilters(), ct));

    [HttpGet("{reference}")]
    [RequireAdminAccess(AdminPermission.RegisteredResidents)]
    public async Task<RegisteredResidentDetails> Details(string reference, CancellationToken ct) =>
        await residents.GetRegisteredResidentAsync(Actor, reference, ct);

    [HttpGet("keys")]
    [RequireAdminAccess(AdminPermission.ReadKeys)]
    public async Task<ResidentKeyPage> Keys(CancellationToken ct) =>
        await residents.ListResidentsWithKeysAsync(Actor, Query("page"), ct);

    [HttpPatch("keys/{owner}/{request}/{index}")]
    [RequireAdminAccess(AdminPermission.EditKeys)]
    public async Task<IActionResult> UpdateKey(string owner, string request, string index, CancellationToken ct)
    {
        if (!long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) || ownerId <= 0 ||
            !long.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId) ||
            !int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Некоректний ключ.");
        await residents.UpdateResidentKeyAsync(Actor, ownerId, requestId, position, await ReadBodyAsync<ResidentKeyUpdate>(ct), ct);
        return Saved();
    }
}
