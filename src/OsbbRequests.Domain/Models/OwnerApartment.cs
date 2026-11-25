namespace OsbbRequests.Domain.Models;

public enum ApartmentReviewStatus { Pending, Approved, Rejected }

public sealed record OwnerApartment(string Id, string BuildingNumber, int ApartmentNumber, DateTimeOffset SubmittedAt,
    ApartmentReviewStatus Status = ApartmentReviewStatus.Pending, DateTimeOffset? ReviewedAt = null, string? ReviewedBy = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Address => $"Будинок {BuildingNumber}, кв. {ApartmentNumber}";
}
