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
    public bool? Administrators { get; set; }

    public void SetPermissionsSnapshot(PagePermissions? permissions)
    {
        ComplexRequests = permissions?.ComplexRequests;
        KeyOrders = permissions?.KeyOrders;
        RegisteredResidents = permissions?.RegisteredResidents;
        PendingResidents = permissions?.PendingResidents;
        Messages = permissions?.Messages;
        Administrators = permissions?.Administrators;
    }
}
