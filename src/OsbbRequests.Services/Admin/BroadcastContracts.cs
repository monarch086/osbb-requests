namespace OsbbRequests.Services.Admin;

public sealed record MessageChange(string Id, string Text);

public sealed record BroadcastMessage(string Id, string Text, string CreatedBy, string CreatedById, DateTimeOffset CreatedAt);
