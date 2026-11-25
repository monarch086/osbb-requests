using OsbbRequests.Domain.Models;

namespace OsbbRequests.Bot.Models;

public enum ProfileAction { Edit, Delete, Account, Apartments, SelectAccount }
public sealed record ResidentProfileDialog(long UserId, string ComplexId, ProfileAction Action, int Version,
    int? ExpectedProfileVersion, ResidentRegistration? Draft, bool Active = true, bool AddingApartment = false, IReadOnlyList<string>? AccountChoices = null);
public sealed record ProfileDialogChange(ResidentProfileDialog Value, int? ExpectedVersion);
public sealed record ProfileUpdate(ResidentRegistration Value, int? ExpectedVersion);
