using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Services;

namespace OsbbRequests.WebApi.Auth;

internal sealed class AdminAccessHandler(AdminAccountService accounts)
    : AuthorizationHandler<AdminAccessRequirement, HttpContext>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminAccessRequirement requirement,
        HttpContext resource)
    {
        if (context.HasFailed || context.User.Identity?.IsAuthenticated != true || !CognitoAuthentication.HasAdminScope(context.User) ||
            context.User.FindFirstValue("sub") is not { } userId)
            return;

        try
        {
            var actor = resource.Features.Get<AdminUser>();
            if (actor is null)
            {
                actor = await accounts.GetActiveAdminAsync(userId, resource.RequestAborted);
                resource.Features.Set(actor);
            }

            CheckPermission(requirement.Permission, actor, resource.Request.RouteValues);
            context.Succeed(requirement);
        }
        catch (AdminOperationException error) when (error.Failure == AdminFailure.Forbidden)
        {
            context.Fail(new AuthorizationFailureReason(this, error.Message));
        }
    }

    private static void CheckPermission(AdminPermission permission, AdminUser actor, RouteValueDictionary routeValues)
    {
        switch (permission)
        {
            case AdminPermission.Active: break;
            case AdminPermission.Messages: MessageService.RequireMessagesAccess(actor); break;
            case AdminPermission.ManageAdministrators: PermissionService.RequireAdministratorManagement(actor); break;
            case AdminPermission.EditPermissions: PermissionService.RequirePermissionsEdit(actor); break;
            case AdminPermission.EditNotifications:
                var target = routeValues["target"] as string ??
                    throw new InvalidOperationException("Notification authorization requires a target route value.");
                PermissionService.RequireNotificationEdit(actor, target);
                break;
            case AdminPermission.RegisteredResidents: ResidentService.RequireResidentDirectoryAccess(actor, pending: false); break;
            case AdminPermission.PendingDirectory: ResidentService.RequireResidentDirectoryAccess(actor, pending: true); break;
            case AdminPermission.ReviewRegistrations: ResidentService.RequireRegistrationReview(actor); break;
            case AdminPermission.RegistrationHistory: ResidentService.RequireRegistrationHistoryAccess(actor); break;
            case AdminPermission.ReviewApartments: ResidentService.RequireApartmentReview(actor); break;
            case AdminPermission.ReadKeys: ResidentService.RequireKeyRead(actor); break;
            case AdminPermission.EditKeys: ResidentService.RequireKeyEdit(actor); break;
            case AdminPermission.Assignees: RequestService.RequireAssigneesAccess(actor); break;
            case AdminPermission.Statistics: StatisticsService.RequireStatisticsAccess(actor); break;
            default: throw new ArgumentOutOfRangeException(nameof(permission), permission, "Unknown administrative permission.");
        }
    }
}
