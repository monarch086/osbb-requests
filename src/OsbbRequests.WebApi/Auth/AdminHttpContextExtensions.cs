using OsbbRequests.Services.Admin;

namespace OsbbRequests.WebApi.Auth;

internal static class AdminHttpContextExtensions
{
    public static AdminUser GetAdminActor(this HttpContext context) =>
        context.Features.Get<AdminUser>() ??
        throw new InvalidOperationException("The administrative authorization requirement did not load an account.");
}
