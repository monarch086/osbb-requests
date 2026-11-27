using System.Globalization;

namespace OsbbRequests.Shared.Residents;

public static class ResidentNameOrder
{
    private static readonly CompareInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA").CompareInfo;

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
}
