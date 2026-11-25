using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin/administrators")]
public sealed class AdministratorsController(PermissionService permissions) : AdminControllerBase
{
    [HttpGet]
    [RequireAdminAccess(AdminPermission.ManageAdministrators)]
    public async Task<Page<AdminUser>> List([FromQuery] string? cursor, CancellationToken ct) =>
        await permissions.ListAdminsAsync(Actor, cursor, ct);

    [HttpPatch("{target}")]
    [RequireAdminAccess(AdminPermission.ManageAdministrators)]
    public async Task<IActionResult> UpdateAccess([FromRoute] string target, [FromBody] AccessChange change, CancellationToken ct)
    {
        await permissions.SetAccessAsync(Actor, target, change, ct);
        return Saved();
    }

    [HttpPatch("{target}/permissions")]
    [RequireAdminAccess(AdminPermission.EditPermissions)]
    public async Task<IActionResult> UpdatePermissions([FromRoute] string target, [FromBody] PermissionsChange change, CancellationToken ct)
    {
        await permissions.SetPermissionsAsync(Actor, target, change, ct);
        return Saved();
    }

    [HttpPatch("{target}/notifications")]
    [RequireAdminAccess(AdminPermission.EditNotifications)]
    public async Task<IActionResult> UpdateNotifications([FromRoute] string target, [FromBody] NotificationSettingsChange change, CancellationToken ct)
    {
        await permissions.SetNotificationSettingsAsync(Actor, target, change, ct);
        return Saved();
    }
}
