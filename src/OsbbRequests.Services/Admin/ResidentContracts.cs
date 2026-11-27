using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services.Admin;

public sealed record ResidentCounts(int Total, int Owners, int FamilyMembers, int Tenants, int Employees);

public sealed record ResidentListRow(string Id, string? FirstName, string? LastName, ResidentType ResidentType, string? BuildingNumber, string? ApartmentNumber);

public sealed record CompactResidentPage(IReadOnlyList<ResidentListRow> Items, int PageNumber, int PageSize, int Total, ResidentCounts Counts, string? StartCursor, string? NextCursor) : IResidentDirectoryView;

public sealed record ResidentDirectoryPage(IReadOnlyList<ResidentProfile> Items, int PageNumber, int PageSize, int Total, ResidentCounts Counts);

public sealed record RegisteredResident(string Id, string? FirstName, string? LastName, string? Phone, DateTimeOffset RegisteredAt, string? TelegramChatId = null, string? BuildingNumber = null, string? ApartmentNumber = null, bool PersonalDataDeleted = false, string? Email = null, DateTimeOffset? ConsentAt = null, ResidentType ResidentType = ResidentType.FamilyMember, int TypeChangeCount = 0, int Version = 0);

public sealed record RegistrationDecision(bool Approved, string? Reason, int Version);

public sealed record RegistrationApproval(string Actor, DateTimeOffset? CreatedAt, bool Automatic);

public sealed record ResidentTypeChangeRequest(string ResidentType, int Version);

public sealed record ResidentDataChangeRequest(string FirstName, string LastName, string ResidentType, int Version);

public sealed record ResidentTypeChange(string? PreviousType, string? NewType, string Actor, DateTimeOffset CreatedAt, bool FirstNameChanged = false, bool LastNameChanged = false, ResidentKeyChange? KeyChange = null);

public sealed record ResidentKeyChange(long RequestId, ResidentKeyDetails Previous, ResidentKeyDetails? Current);

public sealed record ResidentKeyDetails(string Type, string? SerialNumber);

public sealed record ResidentKeyUpdate(int? Version, bool Remove = false, string? Type = null, string? SerialNumber = null);

public sealed record ResidentKeyEntry(string Phone, IReadOnlyList<KeyResident> Residents, IReadOnlyList<ResidentKey> Keys);

public sealed record ResidentKey(string Type, string? SerialNumber, long RequestId, int KeyIndex, long OwnerId, int Version);

public sealed record ResidentKeyPage(IReadOnlyList<ResidentKeyEntry> Items, int PageNumber, int PageSize, int Total);

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
