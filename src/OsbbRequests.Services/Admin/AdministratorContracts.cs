namespace OsbbRequests.Services.Admin;

public sealed record AdminUser(string UserId, string Email, string DisplayName, bool IsActive, bool IsSuperAdmin, int Version,
    string? TelegramChatId = null, bool NotifyNewResidents = false, bool NotifyNewRequests = false, bool NotifyNewAdministrators = false, PagePermissions? Permissions = null, bool NotifyNewKeyOrders = false, bool NotifyNewApartmentRegistrations = false)
{
    public PagePermissions EffectivePermissions => IsSuperAdmin ? PagePermissions.All : Permissions ?? PagePermissions.Legacy;
}

public sealed record PagePermissions(bool ComplexRequests, bool KeyOrders, bool RegisteredResidents, bool PendingResidents,
    bool Messages, bool Administrators = false)
{
    public static readonly PagePermissions Legacy = new(true, true, true, true, false);
    public static readonly PagePermissions All = new(true, true, true, true, true, true);
}

public sealed record PermissionsChange(int Version, PagePermissions Permissions);

public sealed record NotificationSettingsChange(string? TelegramChatId, bool NotifyNewResidents, bool NotifyNewRequests, bool NotifyNewAdministrators, int Version, bool? NotifyNewKeyOrders = null, bool? NotifyNewApartmentRegistrations = null);

public sealed record Identity(string UserId, string Email, string DisplayName);

public sealed record AccessChange(bool IsActive, int Version);

public sealed record Assignee(string UserId, string Email);
