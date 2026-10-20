using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Models;

public enum RequestDraftStage { Scope, Floor, Category, Location, Description, Completed, Cancelled }
public sealed record RequestDraft(long UserId, string ComplexId, RequestDraftStage Stage, int Version,
    RequestScope? Scope = null, string? BuildingNumber = null, int? Floor = null,
    string? Category = null, string? Location = null);
public sealed record RequestDraftChange(RequestDraft Value, int? ExpectedVersion);
