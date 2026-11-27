namespace OsbbRequests.Services.Admin;

public sealed record DailyStatistic(string Date, string Endpoint, long Requests, double ReadUnits, double WriteUnits, double DurationMs);

public sealed record StatisticsReport(IReadOnlyList<DailyStatistic> Items, int Days);
