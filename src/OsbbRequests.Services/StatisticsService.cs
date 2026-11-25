using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public sealed class StatisticsService(IStatisticsReader? statistics)
{
    public async Task<StatisticsReport> GetStatisticsAsync(AdminUser actor, string? period, CancellationToken ct)
    {
        RequireStatisticsAccess(actor);
        if (!int.TryParse(period ?? "7", out var days) || days is not (1 or 7 or 30))
            throw new AdminOperationException(AdminFailure.InvalidInput, "Оберіть період: день, тиждень або місяць.");
        if (statistics is null) throw new AdminOperationException(AdminFailure.Unavailable, "Статистика тимчасово недоступна.");
        return new(await statistics.ReadAsync(days, ct), days);
    }

    public static void RequireStatisticsAccess(AdminUser actor)
    {
        AdminAccess.RequireActive(actor);
        if (!actor.IsSuperAdmin) throw new AdminOperationException(AdminFailure.Forbidden, "Статистика доступна лише супер адміністратору.");
    }
}
