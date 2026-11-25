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
    public async Task<Page<AdminUser>> List(CancellationToken ct) =>
        await permissions.ListAdminsAsync(Actor, Query("cursor"), ct);

    [HttpPatch("{target}")]
    [RequireAdminAccess(AdminPermission.ManageAdministrators)]
    public async Task<IActionResult> UpdateAccess(string target, CancellationToken ct)
    {
        await permissions.SetAccessAsync(Actor, target, await ReadBodyAsync<AccessChange>(ct), ct);
        return Saved();
    }

    [HttpPatch("{target}/permissions")]
    [RequireAdminAccess(AdminPermission.EditPermissions)]
    public async Task<IActionResult> UpdatePermissions(string target, CancellationToken ct)
    {
        await permissions.SetPermissionsAsync(Actor, target, await ReadBodyAsync<PermissionsChange>(ct), ct);
        return Saved();
    }

    [HttpPatch("{target}/notifications")]
    [RequireAdminAccess(AdminPermission.EditNotifications)]
    public async Task<IActionResult> UpdateNotifications(string target, CancellationToken ct)
    {
        await permissions.SetNotificationSettingsAsync(Actor, target, await ReadBodyAsync<NotificationSettingsChange>(ct), ct);
        return Saved();
    }
}
