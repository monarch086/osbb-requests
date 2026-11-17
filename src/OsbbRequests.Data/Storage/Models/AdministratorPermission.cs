namespace OsbbRequests.Data.Storage;

public sealed class AdministratorPermission
{
    public required string Source { get; set; }
    public required string UserId { get; set; }
    public bool ComplexRequests { get; set; }
    public bool KeyOrders { get; set; }
    public bool RegisteredResidents { get; set; }
    public bool PendingResidents { get; set; }
    public bool Messages { get; set; }
}
