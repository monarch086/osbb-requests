using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/administrators")]
[RequireAdminAccess]
public sealed class AdministratorsController(PermissionService permissions) : ControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.ManageAdministrators)]
    public async Task<Page<AdminUser>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await permissions.ListAdminsAsync(HttpContext.GetAdminActor(), cursor, ct);

    [HttpPatch("{target}")]
    [RequireAdminAccess(AdminPermission.ManageAdministrators)]
    public async Task<IActionResult> UpdateAccess([FromRoute] string target, [FromBody] AccessChange change, CancellationToken ct)
    {
        await permissions.SetAccessAsync(HttpContext.GetAdminActor(), target, change, ct);
        return AdminResponses.Saved();
    }

    [HttpPatch("{target}/permissions")]
    [RequireAdminAccess(AdminPermission.EditPermissions)]
    public async Task<IActionResult> UpdatePermissions([FromRoute] string target, [FromBody] PermissionsChange change, CancellationToken ct)
    {
        await permissions.SetPermissionsAsync(HttpContext.GetAdminActor(), target, change, ct);
        return AdminResponses.Saved();
    }

    [HttpPatch("{target}/notifications")]
    [RequireAdminAccess(AdminPermission.EditNotifications)]
    public async Task<IActionResult> UpdateNotifications([FromRoute] string target, [FromBody] NotificationSettingsChange change, CancellationToken ct)
    {
        await permissions.SetNotificationSettingsAsync(HttpContext.GetAdminActor(), target, change, ct);
        return AdminResponses.Saved();
    }
}
