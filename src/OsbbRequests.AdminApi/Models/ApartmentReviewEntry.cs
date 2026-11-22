using OsbbRequests.Shared.Residents;

namespace OsbbRequests.AdminApi.Models;

public sealed record ApartmentReviewEntry(string RegistrationId, string? FirstName, string? LastName,
    IReadOnlyList<OwnerApartment> Apartments);
public sealed record ApartmentReviewDecision(bool? Approved);
