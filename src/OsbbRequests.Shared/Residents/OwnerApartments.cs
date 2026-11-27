using System.Globalization;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Shared.Residents;

public sealed record OwnerApartments(string ComplexId, long OwnerId, string RegistrationId, int Version, IReadOnlyList<OwnerApartment> Items)
{
    public const int MaxAdditional = 10;
    public static string Key(string complexId, long ownerId) =>
        FormattableString.Invariant($"COMPLEX#{complexId}#OWNER_APARTMENTS#{ownerId}");

    public static string ReviewGroup(string complexId) => "APARTMENT_REVIEWS#" + complexId;

    public static bool CanManage(ResidentRegistration? profile) =>
        profile is { Stage: RegistrationStage.Approved, ResidentType: ResidentType.Owner, PersonalDataDeleted: false };
}
