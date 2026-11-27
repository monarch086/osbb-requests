namespace OsbbRequests.Domain.Models;

public sealed record OwnerApartments(string ComplexId, long OwnerId, string RegistrationId, int Version, IReadOnlyList<OwnerApartment> Items)
{
    public const int MaxAdditional = 10;
    public static bool CanManage(ResidentProfile? profile) =>
        profile?.IsApprovedOwner() == true;
}
