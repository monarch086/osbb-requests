using System.Globalization;
using System.Text;

namespace OsbbRequests.Shared.Residents;

public static class ResidentNameOrder
{
    public const int MaxKeyBytes = 1024;

    private static readonly CompareInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA").CompareInfo;

    public static (byte[] Ascending, byte[] Descending) Create(string id, string? firstName, string? lastName)
    {
        var surname = lastName?.Trim() ?? "";
        var idBytes = Encoding.UTF8.GetBytes(id);
        // Unnamed records always come last and use only the registration ID.
        if (surname.Length == 0)
        {
            byte[] unnamed = [1, .. idBytes];
            return (unnamed, unnamed);
        }

        // ICU includes a zero terminator. Complementing each whole component,
        // including its terminator, reverses both unequal and prefix comparisons.
        var last = Ukrainian.GetSortKey(surname, CompareOptions.IgnoreCase).KeyData;
        var first = Ukrainian.GetSortKey(firstName ?? "", CompareOptions.IgnoreCase).KeyData;
        return ([0, .. last, .. first, .. idBytes],
            [0, .. last.Select(Invert), .. first.Select(Invert), .. idBytes]);
    }

    public static int Compare(string leftId, string? leftFirst, string? leftLast,
        string rightId, string? rightFirst, string? rightLast, bool ascending)
    {
        var left = leftLast?.Trim() ?? "";
        var right = rightLast?.Trim() ?? "";
        if ((left.Length == 0) != (right.Length == 0)) return left.Length == 0 ? 1 : -1;
        var result = left.Length == 0 ? 0 : Ukrainian.Compare(left, right, CompareOptions.IgnoreCase);
        if (result == 0 && left.Length > 0)
            result = Ukrainian.Compare(leftFirst ?? "", rightFirst ?? "", CompareOptions.IgnoreCase);
        return result == 0 ? string.CompareOrdinal(leftId, rightId) : result * (ascending ? 1 : -1);
    }

    private static byte Invert(byte value) => (byte)~value;
}
