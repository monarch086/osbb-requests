using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Models;

public sealed record MaintenanceRequest(
    long Id, long OwnerId, string BuildingId, string Category, string Location,
    string Description, DateTimeOffset CreatedAt,
    RequestStatus Status = RequestStatus.New,
    RequestVisibility Visibility = RequestVisibility.Private,
    string? Resolution = null);

public sealed record RequestPage(IReadOnlyList<MaintenanceRequest> Items, long? NextBeforeId);
public sealed record CommandOutcome(string Reply, MaintenanceRequest? NewRequest = null);
public sealed record StoredReply(long UpdateId, long UserId, long ChatId, string Text, bool Delivered);
