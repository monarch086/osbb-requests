using OsbbRequests.Services.Admin;

namespace OsbbRequests.Services;

public interface IStatisticsReader
{
    Task<IReadOnlyList<DailyStatistic>> ReadAsync(int days, CancellationToken ct);
}

public interface IStatisticsStore : IStatisticsReader
{
    Task RecordAsync(string endpoint, double read, double write, long durationMs, CancellationToken ct);
}
