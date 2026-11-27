using OsbbRequests.Domain.Admin;

namespace OsbbRequests.DataImport.Storage;

public sealed class ResidentEvent
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string RegistrationId { get; set; }
    public required string EventKey { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public long? KeyRequestId { get; set; }
    public string? PreviousKeyType { get; set; }
    public string? PreviousKeySerialNumber { get; set; }
    public string? CurrentKeyType { get; set; }
    public string? CurrentKeySerialNumber { get; set; }
    public string? Decision { get; set; }
    public string? Reason { get; set; }
    public string? ApprovalMode { get; set; }
    public string? PreviousType { get; set; }
    public string? NewType { get; set; }
    public bool? FirstNameChanged { get; set; }
    public bool? LastNameChanged { get; set; }

    public ResidentTypeChange ToHistory()
    {
        var keyChange = KeyRequestId is { } request && PreviousKeyType is { } previous
            ? new ResidentKeyChange(request, new(previous, PreviousKeySerialNumber),
                CurrentKeyType is { } current ? new(current, CurrentKeySerialNumber) : null)
            : null;
        return new(PreviousType, NewType, Actor ?? "", CreatedAt ?? default,
            FirstNameChanged ?? false, LastNameChanged ?? false, keyChange);
    }
}
