using OsbbRequests.Shared.Models;

namespace OsbbRequests.AdminApi.Models;

public sealed record AdminUser(string UserId, string Email, string DisplayName, bool IsActive, bool IsSuperAdmin, int Version,
    string? TelegramChatId = null, bool NotifyNewResidents = false, bool NotifyNewRequests = false, bool NotifyNewAdministrators = false);
public sealed record NotificationSettingsChange(string? TelegramChatId, bool NotifyNewResidents, bool NotifyNewRequests, bool NotifyNewAdministrators, int Version);
public sealed record Identity(string UserId, string Email, string DisplayName);
public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);
public sealed record AccessChange(bool IsActive, int Version);
public sealed record RegisteredResident(string Id, string? FirstName, string? LastName, string? Phone, DateTimeOffset RegisteredAt, string? TelegramChatId = null);
public sealed record RegistrationDecision(bool Approved, string? Reason, int Version);
public sealed record RequestChange(string Status, string Visibility, string? Resolution, string? AssignedTo,
    string Priority, string? PlannedDate, int Version, string? Scope = null, string? BuildingNumber = null, int? Floor = null);
public sealed record ManagedRequest(MaintenanceRequest Request, int Version);
public sealed record RequestFilters(IReadOnlyList<string>? Statuses, string? Category, string? Search, string? From, string? To, string? Target = null, int? Floor = null);
public sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
