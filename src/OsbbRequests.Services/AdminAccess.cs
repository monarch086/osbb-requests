using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

internal static class AdminAccess
{
    public static AdminUser RequireActive(AdminUser? actor) =>
        actor?.IsActive == true ? actor : throw new AdminOperationException(AdminFailure.Forbidden, "Адміністративний доступ ще не надано.");
}
