using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Shared.Models;

public sealed record MaintenanceRequest(
    long Id, long OwnerId, string BuildingId, string Category, string Location,
    string Description, DateTimeOffset CreatedAt,
    RequestStatus Status = RequestStatus.New,
    RequestVisibility Visibility = RequestVisibility.Private,
    string? Resolution = null, string? AssignedTo = null, string Priority = "Normal", DateOnly? PlannedDate = null,
    RequestScope? Scope = null, string? BuildingNumber = null, int? Floor = null, int? Entrance = null, string? KeyType = null, int? KeyQuantity = null);

public sealed record RequestPage(IReadOnlyList<MaintenanceRequest> Items, long? NextBeforeId);

public static class RequestCategories
{
    public const string KeyOrder = "Замовлення ключів";
}
