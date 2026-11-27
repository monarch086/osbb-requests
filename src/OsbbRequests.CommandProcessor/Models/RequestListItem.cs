using OsbbRequests.Domain;

namespace OsbbRequests.CommandProcessor.Models;

// Deliberately excludes owner/contact data, private correspondence and administrative fields.
public sealed record RequestListItem(long Id, RequestStatus Status, DateTimeOffset CreatedAt,
    RequestScope? Scope, string? BuildingNumber, int? Floor, string Category, string Location, string Description, int? Entrance);
public sealed record RequestListBoundary(long RequestId, DateTimeOffset? CreatedAt = null);
public sealed record DeletableRequest(long Id, long OwnerId, string ComplexId, RequestStatus Status, int Version);
public sealed record RequestDeletion(long Id, long OwnerId, string ComplexId, int ExpectedVersion);
