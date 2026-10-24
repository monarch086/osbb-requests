using System.Text.Json.Serialization;

namespace OsbbRequests.Shared.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<RequestScope>))]
public enum RequestScope { Complex, Building }

public static class ComplexLayout
{
    public static IReadOnlyList<string> BuildingNumbers { get; } = Array.AsReadOnly(new[] { "4Б", "4В", "4Г", "4Д" });
    public const int FloorCount = 25;
    public const int EntranceFloor = 0;
    public static bool IsValid(RequestScope? scope, string? buildingNumber, int? floor) => scope switch
    {
        RequestScope.Complex => buildingNumber is null && floor is null,
        RequestScope.Building => buildingNumber is not null && BuildingNumbers.Contains(buildingNumber, StringComparer.Ordinal) && (floor is null or >= EntranceFloor and <= FloorCount),
        _ => false
    };
    public static string Target(RequestScope? scope, string? buildingNumber) => scope == RequestScope.Complex ? "Complex" :
        scope == RequestScope.Building && buildingNumber is not null && BuildingNumbers.Contains(buildingNumber, StringComparer.Ordinal) ? "Building" + buildingNumber : "Unspecified";
    public static string Label(RequestScope? scope, string? buildingNumber, int? floor) => scope switch
    {
        RequestScope.Complex => "Весь комплекс",
        RequestScope.Building => $"Будинок {buildingNumber}" + (floor == EntranceFloor ? " · Парадне" : floor is { } number ? $", поверх {number}" : " · без прив’язки до поверху"),
        _ => "Будинок не уточнено"
    };
}
