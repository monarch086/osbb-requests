using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Models;

// Preserve numeric values, including the legacy Location stage, for stored drafts.
public enum RequestDraftStage { Scope, Floor, Category, Location, Description, Completed, Cancelled, Entrance }
public sealed record RequestDraft(long UserId, string ComplexId, RequestDraftStage Stage, int Version,
    RequestScope? Scope = null, string? BuildingNumber = null, int? Floor = null,
    string? Category = null, string? Location = null, int? Entrance = null);
public sealed record RequestDraftChange(RequestDraft Value, int? ExpectedVersion);
