using System.Globalization;
using OsbbRequests.Domain.Residents;

namespace OsbbRequests.Shared.Residents;

/// <summary>Stable keys for one resident/phone association; multiple residents may share a phone.</summary>
public static class ResidentPhoneIndex
{
    public const string IndexName = "ResidentsByPhone";

    public static string Normalize(string phone) => PhoneNumber.Normalize(phone);
    public static bool TryNormalize(string? phone, out string? normalized) => PhoneNumber.TryNormalize(phone, out normalized);

    public static string LookupKey(string complexId, string phone)
    {
        if (string.IsNullOrWhiteSpace(complexId) || complexId.Contains('#')) throw new ArgumentException("Invalid complex ID.", nameof(complexId));
        return "COMPLEX#" + complexId + "#PHONE#" + Normalize(phone);
    }

    public static string ItemKey(string complexId, long userId, string phone)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        _ = LookupKey(complexId, phone);
        return "COMPLEX#" + complexId + "#PHONE_ENTRY#" + userId.ToString(CultureInfo.InvariantCulture) + "#" + Normalize(phone);
    }

}
