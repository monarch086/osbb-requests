using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Data.Storage;

public sealed class ResidentApartment
{
    public long SourceId { get; set; }
    public required string ComplexId { get; set; }
    public long TelegramUserId { get; set; }
    public required string ApartmentId { get; set; }
    public required string RegistrationId { get; set; }
    public required string BuildingNumber { get; set; }
    public int ApartmentNumber { get; set; }
    public ApartmentReviewStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
}
