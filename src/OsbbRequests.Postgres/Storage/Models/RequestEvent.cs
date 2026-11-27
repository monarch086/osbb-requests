using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace OsbbRequests.DataImport.Storage;

public sealed class RequestEvent
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public long RequestId { get; set; }
    public required string EventKey { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? Action { get; set; }
    public string? Actor { get; set; }
    public long? ActorId { get; set; }
    public string? Status { get; set; }
    public string? PreviousStatus { get; set; }
    public string? Resolution { get; set; }
    public bool HasResolution { get; set; }
    public string? Target { get; set; }
    public int? Floor { get; set; }
    public bool HasFloor { get; set; }
    public int? Entrance { get; set; }
    public bool HasEntrance { get; set; }
    public RequestEventSnapshot? Snapshot { get; set; }

    // Compatibility with the original DynamoDB importer.
    [NotMapped]
    public string Data
    {
        set
        {
            using var document = JsonDocument.Parse(value);
            var audit = document.RootElement;
            Action = Text(audit, "action");
            Actor = Text(audit, "actor");
            ActorId = long.TryParse(Text(audit, "actorId"), System.Globalization.CultureInfo.InvariantCulture, out var actorId) ? actorId : null;
            Status = Text(audit, "status");
            PreviousStatus = Text(audit, "previousStatus");
            Target = Text(audit, "target");
            HasResolution = audit.TryGetProperty("resolution", out var resolution);
            Resolution = Text(resolution);
            HasFloor = ReadLocation(audit, "floor", "Floor", out var floor);
            Floor = floor;
            HasEntrance = ReadLocation(audit, "entrance", "Entrance", out var entrance);
            Entrance = entrance;
            Snapshot = Text(audit, "changes") is { } changes ? RequestEventSnapshot.FromLegacy(changes) : null;
        }
    }

    private static bool ReadLocation(JsonElement audit, string key, string localKey, out int? number)
    {
        var exists = audit.TryGetProperty(key, out var value) || audit.TryGetProperty(localKey, out value);
        number = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
        return exists;
    }

    private static string? Text(JsonElement audit, string key) => audit.TryGetProperty(key, out var value) ? Text(value) : null;

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };
}
