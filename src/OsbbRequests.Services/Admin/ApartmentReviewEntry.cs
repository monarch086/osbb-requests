using OsbbRequests.Domain.Models;

namespace OsbbRequests.Services.Admin;

public sealed record ApartmentReviewEntry(string RegistrationId, string? FirstName, string? LastName,
    IReadOnlyList<OwnerApartment> Apartments);
public sealed record ApartmentReviewDecision(bool? Approved);
