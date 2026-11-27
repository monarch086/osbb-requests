using OsbbRequests.Domain.Models;

namespace OsbbRequests.Domain.Residents;

public static class ResidentRegistrationPolicy
{
    public static bool CanApproveAutomatically(ResidentType type) =>
        type is ResidentType.FamilyMember or ResidentType.Tenant;
}
