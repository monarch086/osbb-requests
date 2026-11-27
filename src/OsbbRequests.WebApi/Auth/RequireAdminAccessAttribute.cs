using Microsoft.AspNetCore.Authorization;

namespace OsbbRequests.WebApi.Auth;

internal enum AdminPermission
{
    Active,
    Messages,
    ReadAdministrators,
    ManageAdministrators,
    EditPermissions,
    EditNotifications,
    RegisteredResidents,
    PendingDirectory,
    ReviewRegistrations,
    RegistrationHistory,
    ReviewApartments,
    ReadKeys,
    EditKeys,
    Assignees,
    Statistics
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
internal sealed class RequireAdminAccessAttribute(AdminPermission permission = AdminPermission.Active)
    : Attribute, IAuthorizationRequirementData
{
    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return new AdminAccessRequirement(permission);
    }
}

internal sealed record AdminAccessRequirement(AdminPermission Permission) : IAuthorizationRequirement;
