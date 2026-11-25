namespace OsbbRequests.Postgres;

public sealed record PostgresOptions(string Region, string Stage, string Complex, string? AccountId = null);
