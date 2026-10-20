namespace OsbbRequests.Bot.Models;

public enum RequestLookupAction { View, Delete }
public sealed record RequestLookup(long UserId, string ComplexId, bool AwaitingNumber, int Version,
    RequestLookupAction Action = RequestLookupAction.View, long? RequestId = null, int? RequestVersion = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => AwaitingNumber || RequestId.HasValue;
}
public sealed record RequestLookupChange(RequestLookup Value, int? ExpectedVersion);
