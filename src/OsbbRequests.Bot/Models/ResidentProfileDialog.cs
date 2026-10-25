using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Models;

public enum ProfileAction { Edit, Delete }
public sealed record ResidentProfileDialog(long UserId, string ComplexId, ProfileAction Action, int Version,
    int? ExpectedProfileVersion, ResidentRegistration? Draft, bool Active = true);
public sealed record ProfileDialogChange(ResidentProfileDialog Value, int? ExpectedVersion);
public sealed record ProfileUpdate(ResidentRegistration Value, int? ExpectedVersion);
