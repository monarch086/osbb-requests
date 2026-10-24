using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Models;

// Deliberately excludes owner/contact data, private correspondence and administrative fields.
public sealed record PublicRequestSummary(long Id, RequestStatus Status, DateTimeOffset CreatedAt,
    RequestScope? Scope, string? BuildingNumber, int? Floor, string Category, string Location, string Description = "");
public sealed record PublicRequestPage(IReadOnlyList<PublicRequestSummary> Items, string? NextBefore);
public sealed record DeletableRequest(long Id, long OwnerId, string ComplexId, RequestStatus Status, int Version);
public sealed record RequestDeletion(long Id, long OwnerId, string ComplexId, int ExpectedVersion);
