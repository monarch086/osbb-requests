using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using OsbbRequests.Domain.Admin;

namespace OsbbRequests.DataImport.Storage;

public sealed class AdministratorEvent
{
    public long SourceId { get; set; }
    public required string Key { get; set; }
    public required string AdministratorId { get; set; }
    public string? Action { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public bool? ComplexRequests { get; set; }
    public bool? KeyOrders { get; set; }
    public bool? RegisteredResidents { get; set; }
    public bool? PendingResidents { get; set; }
    public bool? Messages { get; set; }

    // The one-time DynamoDB importer assigns the legacy JSON through this property.
    [NotMapped]
    public string Data
    {
        set
        {
            var snapshot = JsonSerializer.Deserialize<ImportedAudit>(value)
                ?? throw new JsonException("Missing administrator audit data.");
            Action = snapshot.Action;
            Actor = snapshot.Actor;
            CreatedAt = snapshot.CreatedAt?.ToUniversalTime();
            SetPermissionsSnapshot(snapshot.Permissions is null ? null : JsonSerializer.Deserialize<PagePermissions>(snapshot.Permissions));
        }
    }

    public void SetPermissionsSnapshot(PagePermissions? permissions)
    {
        ComplexRequests = permissions?.ComplexRequests;
        KeyOrders = permissions?.KeyOrders;
        RegisteredResidents = permissions?.RegisteredResidents;
        PendingResidents = permissions?.PendingResidents;
        Messages = permissions?.Messages;
    }

    private sealed record ImportedAudit(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("permissions")] string? Permissions);
}
