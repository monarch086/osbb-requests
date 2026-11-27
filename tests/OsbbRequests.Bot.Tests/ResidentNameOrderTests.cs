using System.Globalization;
using OsbbRequests.Services.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentNameOrderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeepsUkrainianOrderingAndStableTies(bool ascending)
    {
        var names = new (string Id, string? FirstName, string? LastName)[]
        {
            ("12", "Іван", "Гнатюк"), ("11", "Іван", "ґАЛАҐАН"), ("15", "Іван", "Дорош"),
            ("16", "Анна", "Ґалаґан"), ("02", "анна", " Ґалаґан "), ("19", "A", "Shevchenko"),
            ("10", "І", "Гнатюк"), ("20", "Ї", null), ("01", "Я", " "), ("06", "Єва", "Орлик"),
            ("09", "Іван", "Іванов"), ("08", "Іван", "Їжак"), ("05", "Іван", "Євтушенко"),
            ("14", "Тест", "О’Браєн"), ("13", "Тест", "О-Браєн"), ("07", null, "Дорош")
        };
        var compare = CultureInfo.GetCultureInfo("uk-UA").CompareInfo;
        var expected = names.ToArray();
        Array.Sort(expected, (a, b) =>
        {
            var left = a.LastName?.Trim() ?? "";
            var right = b.LastName?.Trim() ?? "";
            if ((left.Length == 0) != (right.Length == 0)) return left.Length == 0 ? 1 : -1;
            var result = compare.Compare(left, right, CompareOptions.IgnoreCase);
            if (result == 0 && left.Length > 0) result = compare.Compare(a.FirstName ?? "", b.FirstName ?? "", CompareOptions.IgnoreCase);
            return result == 0 ? string.CompareOrdinal(a.Id, b.Id) : result * (ascending ? 1 : -1);
        });
        Array.Sort(names, (a, b) => ResidentNameOrder.Compare(
            a.Id, a.FirstName, a.LastName, b.Id, b.FirstName, b.LastName, ascending));
        Assert.Equal(expected.Select(item => item.Id), names.Select(item => item.Id));
    }
}
