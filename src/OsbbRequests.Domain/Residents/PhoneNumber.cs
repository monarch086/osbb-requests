using System.Text.RegularExpressions;

namespace OsbbRequests.Domain.Residents;

public static partial class PhoneNumber
{
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

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
