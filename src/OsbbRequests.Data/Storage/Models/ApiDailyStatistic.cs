namespace OsbbRequests.Data.Storage;

public sealed class ApiDailyStatistic
{
    public required string Source { get; set; }
    public required string Key { get; set; }
    public required string Date { get; set; }
    public required string Endpoint { get; set; }
    public long Requests { get; set; }
    public double ReadUnits { get; set; }
    public double WriteUnits { get; set; }
    public double DurationMs { get; set; }
}
