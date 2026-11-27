using OsbbRequests.Services.Notifications;
using NotificationEvent = OsbbRequests.Services.Notifications.AdministratorEvent;

namespace OsbbRequests.DataImport.Storage;

public sealed class Notification
{
    public const string PendingKind = "LOCAL_PENDING";
    private const string LocalPrefix = "LOCAL#";

    public long SourceId { get; set; }
    public required string Key { get; set; }
    public long ComplexId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? Lease { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    public string? AudienceAfter { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long? OwnerId { get; set; }
    public long? RequestId { get; set; }
    public int? RequestVersion { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public NotificationEvent? AdministratorEvent { get; set; }
    public string? AdministratorId { get; set; }
    public string? ResidentBroadcastId { get; set; }
    public string? RegistrationId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Disposition { get; set; }
    public DateTimeOffset? LegacyExpiresAt { get; set; }

    public static Notification CreatePending(long sourceId, long complexId, string complex, ResidentNotification value,
        DateTimeOffset? dueAt = null)
    {
        if (value.ComplexId != complex || string.IsNullOrEmpty(value.Id))
            throw new InvalidOperationException("Notification identity mismatch.");
        return new()
        {
            SourceId = sourceId,
            ComplexId = complexId,
            Key = LocalPrefix + value.Id,
            Kind = PendingKind,
            DueAt = dueAt,
            OwnerId = value.OwnerId,
            RequestId = value.RequestId,
            RequestVersion = value.RequestVersion,
            Text = value.Text,
            CreatedAt = value.CreatedAt,
            AdministratorEvent = value.AdministratorEvent,
            AdministratorId = value.AdministratorId,
            ResidentBroadcastId = value.ResidentBroadcastId,
            RegistrationId = value.RegistrationId
        };
    }

    public ResidentNotification ToPending(string complex)
    {
        if (!Key.StartsWith(LocalPrefix, StringComparison.Ordinal) || Key.Length == LocalPrefix.Length ||
            OwnerId is not { } owner || Text is not { } text || CreatedAt is not { } created)
        {
            throw new InvalidOperationException("Missing notification delivery fields.");
        }

        return new(Key[LocalPrefix.Length..], complex, owner, RequestId, RequestVersion, text, created,
            AdministratorEvent, AdministratorId, ResidentBroadcastId, RegistrationId);
    }
}
