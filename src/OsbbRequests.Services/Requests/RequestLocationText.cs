using OsbbRequests.Domain;

namespace OsbbRequests.Services.Requests;

public static class RequestLocationText
{
    public const string ComplexLabel = "Прибудинкова територія";

    public static string Format(RequestScope? scope, string? buildingNumber, int? floor, int? entrance = null) => scope switch
    {
        RequestScope.Complex => ComplexLabel,
        RequestScope.Building => $"Будинок {buildingNumber} · під’їзд {(entrance is { } entry ? entry.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Не зазначено")}" + (floor == ComplexLayout.EntranceFloor ? " · Парадне" : floor is { } number ? $", поверх {number}" : " · без прив’язки до поверху"),
        _ => "Будинок не уточнено"
    };
}
