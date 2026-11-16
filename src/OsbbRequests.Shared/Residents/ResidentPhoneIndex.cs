using System.Globalization;
using System.Text.RegularExpressions;

namespace OsbbRequests.Shared.Residents;

/// <summary>Stable keys for one resident/phone association; multiple residents may share a phone.</summary>
public static partial class ResidentPhoneIndex
{
    public const string IndexName = "ResidentsByPhone";

    public static string Normalize(string phone)
    {
        var value = string.Concat(phone.Trim().Where(c => c is not (' ' or '(' or ')' or '-')));
        if (value.StartsWith('0')) value = "+38" + value;
        else if (!value.StartsWith('+')) value = "+" + value;
        return PhonePattern().IsMatch(value) ? value : throw new ArgumentException("Invalid phone number.", nameof(phone));
    }

    public static bool TryNormalize(string? phone, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(phone)) return false;
        try { normalized = Normalize(phone); return true; }
        catch (ArgumentException) { return false; }
    }

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

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
