using OsbbRequests.Domain;

namespace OsbbRequests.DataImport.Storage;

public sealed class Request
{
    public long SourceId { get; set; }
    public long OwnerId { get; set; }
    public long RequestId { get; set; }
    public int Version { get; set; }
    public long ComplexId { get; set; }
    public RequestStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Category { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public RequestVisibility Visibility { get; set; }
    public string? Resolution { get; set; }
    public string? AssignedTo { get; set; }
    public string Priority { get; set; } = "Normal";
    public DateOnly? PlannedDate { get; set; }
    public RequestScope? Scope { get; set; }
    public string? BuildingNumber { get; set; }
    public int? Floor { get; set; }
    public int? Entrance { get; set; }
    public string? KeyType { get; set; }
    public int? KeyQuantity { get; set; }
}
