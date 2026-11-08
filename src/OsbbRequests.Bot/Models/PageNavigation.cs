namespace OsbbRequests.Bot.Models;

public sealed record PageNavigation(long UserId, string ComplexId, string? NextCommand, int Version, long ExpiresAt, bool ReturnToMenu = false);
public sealed record PageNavigationChange(PageNavigation Value, int? ExpectedVersion);
