using OsbbRequests.Domain.Models;

namespace OsbbRequests.Domain.Admin;

public sealed record Assignee(string UserId, string Email);
public sealed record StatisticsReport(IReadOnlyList<DailyStatistic> Items, int Days);
public sealed record PendingResident(string Id, string? FirstName, string? LastName, string? Phone, string? Email,
    string? BuildingNumber, string? ApartmentNumber, ResidentType ResidentType, int TypeChangeCount,
    DateTimeOffset? ConsentAt, DateTimeOffset UpdatedAt, int Version, string TelegramChatId);
public sealed record RegisteredResidentDetails(RegisteredResident Resident, RegistrationApproval? Approval, IReadOnlyList<OwnerApartment> Apartments);

public interface IResidentDirectoryView
{
    int PageNumber { get; }
    int PageSize { get; }
    int Total { get; }
    ResidentCounts Counts { get; }
}

public sealed record ResidentDirectoryView<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total, ResidentCounts Counts) : IResidentDirectoryView;
public sealed record ResidentDirectoryFilters(string? Building = null, string? ResidentType = null, string? Sort = null, string? Direction = null);
public sealed record RequestSearch(string? Status = null, string? Category = null, string? Search = null,
    string? From = null, string? To = null, string? Target = null, string? Floor = null, string? Section = null);
