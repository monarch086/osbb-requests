using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[ApiController]
[Route("admin")]
[Authorize(Policy = CognitoAuthentication.AdminScope)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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
