namespace OsbbRequests.Services.Admin;

public sealed record Page<T>(IReadOnlyList<T> Items, string? Cursor);
