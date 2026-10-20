using System.Text.Json.Serialization;

namespace OsbbRequests.Bot.Models;

[JsonConverter(typeof(JsonStringEnumConverter<RegistrationStage>))]
public enum RegistrationStage
{
    AwaitingPhone,
    AwaitingFirstName,
    AwaitingLastName,
    Pending,
    Approved,
    Rejected,
    Cancelled
}

public enum ReplyKeyboard
{
    RequestContact,
    Remove
}

public sealed record ResidentRegistration(
    string Id, long UserId, string BuildingId, RegistrationStage Stage, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Phone = null, string? FirstName = null, string? LastName = null, string? DecisionReason = null);

public sealed record RegistrationChange(ResidentRegistration Value, int? ExpectedVersion);
