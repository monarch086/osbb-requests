namespace OsbbRequests.Domain.Models;

public sealed record MaintenanceRequest(
    long Id, long OwnerId, string BuildingId, string Category, string Location,
    string Description, DateTimeOffset CreatedAt,
    RequestStatus Status = RequestStatus.New,
    RequestVisibility Visibility = RequestVisibility.Private,
    string? Resolution = null, string? AssignedTo = null, string Priority = "Normal", DateOnly? PlannedDate = null,
    RequestScope? Scope = null, string? BuildingNumber = null, int? Floor = null, int? Entrance = null, string? KeyType = null, int? KeyQuantity = null, IReadOnlyList<KeyOrderItem>? KeyItems = null);

public sealed record KeyOrderItem(string Type, string Phone, string? SerialNumber = null);

public static class RequestCategories
{
    public const string KeyOrder = "Замовлення ключів";
}
