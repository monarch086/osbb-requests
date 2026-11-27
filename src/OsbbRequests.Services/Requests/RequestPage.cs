using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services.Requests;

public sealed record RequestPage(IReadOnlyList<MaintenanceRequest> Items, long? NextBeforeId);
