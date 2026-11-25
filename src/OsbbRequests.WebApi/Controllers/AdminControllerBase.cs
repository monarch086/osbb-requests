using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OsbbRequests.Domain.Admin;
using OsbbRequests.WebApi.Auth;

namespace OsbbRequests.WebApi.Controllers;

[ApiController]
[Authorize(Policy = CognitoAuthentication.AdminScope)]
[RequireAdminAccess]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(30_003)] // Up to three UTF-8 bytes per UTF-16 code unit in the previous limit, plus a BOM.
public abstract class AdminControllerBase : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    protected AdminUser Actor => HttpContext.Features.Get<AdminUser>() ??
        throw new InvalidOperationException("The administrative authorization requirement did not load an account.");

    protected OkObjectResult Saved() => Ok(new { ok = true });

    protected FileContentResult Workbook(byte[] contents) => File(contents, ExcelContentType);
}
