namespace OsbbRequests.Domain.Admin;

public sealed record DailyStatistic(string Date, string Endpoint, long Requests, double ReadUnits, double WriteUnits, double DurationMs);
