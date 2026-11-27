using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace OsbbRequests.WebApi.Auth;

internal sealed class AdminAccessResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var reason = result.Forbidden
            ? result.AuthorizationFailure?.FailureReasons.FirstOrDefault(reason => reason.Handler is AdminAccessHandler)
            : null;
        if (reason is null)
        {
            await defaultHandler.HandleAsync(next, context, policy, result);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = reason.Message }, context.RequestAborted);
    }
}
