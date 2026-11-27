using System.Globalization;

namespace OsbbRequests.Domain.Residents;

public static class PersonalAccountNumber
{
    public static bool IsBuilding(string? value) => ComplexLayout.BuildingNumbers.Contains(value, StringComparer.Ordinal);

    public static bool TryParseApartment(string? value, out int apartment) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out apartment) && apartment is >= 1 and <= 999;

    public static bool TryCreate(string? building, string? value, out string account)
    {
        account = "";
        if (!IsBuilding(building) || !TryParseApartment(value, out var apartment)) return false;
        account = Format(building!, apartment);
        return true;
    }

    public static string Format(string building, int apartment) =>
        FormattableString.Invariant($"000219{building.ToLowerInvariant()}{apartment:D3}");
}
