using OsbbRequests.Domain.Admin;

namespace OsbbRequests.Services;

public interface IStatisticsStore
{
    Task<IReadOnlyList<DailyStatistic>> ReadAsync(int days, CancellationToken ct);
    Task RecordAsync(string endpoint, double read, double write, long durationMs, CancellationToken ct);
}
