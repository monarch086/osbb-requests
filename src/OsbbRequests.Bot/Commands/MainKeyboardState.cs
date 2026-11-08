using OsbbRequests.Bot.Persistence;

namespace OsbbRequests.Bot.Commands;

public static class MainKeyboardState
{
    public static async Task<bool> HasActiveDialogAsync(ICommandRepository repository, long userId, string complexId, bool registered, CancellationToken ct, bool ignoreRequestDraft = false)
    {
        if (!registered)
        {
            var registration = await repository.GetRegistrationAsync(userId, ct);
            return registration is not null && registration.BuildingId == complexId && RegistrationService.IsDraft(registration.Stage);
        }
        var profile = await repository.GetProfileDialogAsync(userId, ct);
        if (profile is { Active: true } && profile.ComplexId == complexId) return true;
        var lookup = await repository.GetRequestLookupAsync(userId, ct);
        if (lookup is { IsActive: true } && lookup.ComplexId == complexId) return true;
        if (ignoreRequestDraft) return false;
        var draft = await repository.GetRequestDraftAsync(userId, ct);
        return draft is not null && draft.ComplexId == complexId && RequestCreationService.IsActive(draft);
    }
}
