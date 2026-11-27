using System.Text.Json.Serialization;

namespace OsbbRequests.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<RequestScope>))]
public enum RequestScope { Complex, Building }

public static class ComplexLayout
{
    public static IReadOnlyList<string> BuildingNumbers { get; } = Array.AsReadOnly(new[] { "4Б", "4В", "4Г", "4Д" });
    public const int FloorCount = 25;
    public const int EntranceFloor = 0;
    public static bool IsValid(RequestScope? scope, string? buildingNumber, int? floor, int? entrance = null) => scope switch
    {
        RequestScope.Complex => buildingNumber is null && floor is null && entrance is null,
        RequestScope.Building => buildingNumber is not null && BuildingNumbers.Contains(buildingNumber, StringComparer.Ordinal) && (floor is null or >= EntranceFloor and <= FloorCount) &&
            (entrance is null or 1 or 2) && (buildingNumber != "4Б" || entrance is null or 1),
        _ => false
    };
    public static bool IsValidRequest(string category, RequestScope? scope, string? buildingNumber, int? floor, int? entrance = null) =>
        category == OsbbRequests.Domain.Models.RequestCategories.KeyOrder && scope is null && buildingNumber is null && floor is null && entrance is null ||
        IsValid(scope, buildingNumber, floor, entrance);
}
