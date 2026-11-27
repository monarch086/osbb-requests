using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services.Admin;

public sealed record RequestChange(string Status, string Visibility, string? Resolution, string? AssignedTo,
    string Priority, string? PlannedDate, int Version, string? Scope = null, string? BuildingNumber = null, int? Floor = null, int? Entrance = null, IReadOnlyList<string?>? KeySerialNumbers = null);

public sealed record RequestAuthor(string? FirstName, string? LastName, string? BuildingNumber, string? ApartmentNumber, bool PersonalDataDeleted = false, string? Phone = null);

public sealed record KeyResident(string Name, string? BuildingNumber, string? ApartmentNumber);

public sealed record ManagedRequest(MaintenanceRequest Request, int Version, RequestAuthor? Author = null,
    [property: Obsolete("Use KeyResidents; retained temporarily for older frontend versions.")] IReadOnlyList<IReadOnlyList<string>>? KeyResidentNames = null,
    IReadOnlyList<IReadOnlyList<KeyResident>>? KeyResidents = null);

public sealed record RequestCounts(int Total, int NewCount, int ProgressCount);

public sealed record AdminRequestPage(IReadOnlyList<ManagedRequest> Items, string? Cursor, int PageNumber, int PageSize, RequestCounts? Counts);

public sealed record RequestFilters(IReadOnlyList<string>? Statuses, string? Category, string? Search, string? From, string? To, string? Target = null, int? Floor = null, string? Section = null);

public sealed record RequestSearch(string? Status = null, string? Category = null, string? Search = null,
    string? From = null, string? To = null, string? Target = null, string? Floor = null, string? Section = null);
