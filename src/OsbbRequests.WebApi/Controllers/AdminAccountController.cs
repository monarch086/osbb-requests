using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Services;
using OsbbRequests.Services.Admin;

namespace OsbbRequests.WebApi.Controllers;

[Route("admin")]
public sealed class AdminAccountController(AdminAccountService accounts) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<AdminUser>> Me(CancellationToken ct)
    {
        var userId = User.FindFirstValue("sub")!;
        var token = await HttpContext.GetTokenAsync(JwtBearerDefaults.AuthenticationScheme, "access_token");
        return await accounts.GetOrRegisterAdminAsync(userId, token, ct);
    }
}
