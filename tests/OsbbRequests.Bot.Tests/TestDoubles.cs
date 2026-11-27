using System.Runtime.CompilerServices;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Persistence;
using OsbbRequests.CommandProcessor.Telegram;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services.Residents;

namespace OsbbRequests.Bot.Tests;

internal sealed class RecordingServiceNotifier(bool fail = false) : IServiceNotifier
{
    public List<(string Operation, string ErrorType, string? RequestId)> Errors { get; } = [];
    public Task NotifyFailureAsync(string operation, Exception error, string? requestId = null, Action<string>? log = null)
    {
        Errors.Add((operation, error.GetType().Name, requestId));
        if (fail) throw new InvalidOperationException("Notifier unavailable");
        return Task.CompletedTask;
    }
}

internal sealed class MemoryRepository : ICommandRepository
{
    public Dictionary<(long UserId, long MessageId), long> NotificationRequests { get; } = [];
    public Task<long?> GetNotificationRequestAsync(long userId, long messageId, CancellationToken cancellationToken) =>
        Task.FromResult(NotificationRequests.TryGetValue((userId, messageId), out var id) ? (long?)id : null);
    public Dictionary<long, PageNavigation> PageNavigations { get; } = [];
    public Task<PageNavigation?> GetPageNavigationAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(PageNavigations.GetValueOrDefault(userId));
    public Dictionary<(long UserId, string ComplexId, string Token), string> ComplexCursors { get; } = [];
    public Task<string?> GetComplexCursorAsync(long userId, string complexId, string token, CancellationToken ct) =>
        Task.FromResult(ComplexCursors.GetValueOrDefault((userId, complexId, token)));
    public RequestListBoundary? LastRequestBoundary { get; private set; }
    public Dictionary<long, int> RequestVersions { get; } = [];
    public Task<DeletableRequest?> GetDeletableRequestAsync(long userId, long requestId, CancellationToken ct)
    {
        var request = Requests.FirstOrDefault(r => r.OwnerId == userId && r.Id == requestId);
        return Task.FromResult(request is null ? null : new DeletableRequest(request.Id, request.OwnerId,
            request.BuildingId, request.Status, RequestVersions.GetValueOrDefault(request.Id, 1)));
    }
    public async IAsyncEnumerable<RequestListItem> ReadRequestListAsync(string complexId, long? ownerId,
        RequestListBoundary? before, long[]? pageIds, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        LastRequestBoundary = before;
        Queries++;
        var candidates = Requests.Where(r => r.BuildingId == complexId && (pageIds is null || pageIds.Contains(r.Id)));
        candidates = ownerId is { } owner
            ? candidates.Where(r => r.OwnerId == owner).OrderByDescending(r => r.Id)
            : candidates.Where(r => r.Visibility == RequestVisibility.Public && r.Status is RequestStatus.New or RequestStatus.InProgress)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
        if (before is { CreatedAt: { } date } publicBoundary)
            candidates = candidates.Where(r => r.CreatedAt < date || r.CreatedAt == date && r.Id < publicBoundary.RequestId);
        else if (before is { } personalBoundary)
            candidates = candidates.Where(r => r.Id < personalBoundary.RequestId);

        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested();
            yield return new(item.Id, item.Status, item.CreatedAt, item.Scope, item.BuildingNumber, item.Floor,
                item.Category, item.Location, item.Description, item.Entrance);
        }
    }
    public HashSet<long> Residents { get; } = [42];
    public Dictionary<long, StoredReply> Replies { get; } = [];
    public List<MaintenanceRequest> Requests { get; } = [];
    public Dictionary<long, RequestLookup> Lookups { get; } = [];
    public Task<RequestLookup?> GetRequestLookupAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(Lookups.GetValueOrDefault(userId));
    public Dictionary<long, RequestDraft> Drafts { get; } = [];
    public Task<RequestDraft?> GetRequestDraftAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(Drafts.GetValueOrDefault(userId));
    public Dictionary<long, OsbbRequests.Domain.Models.OwnerApartments> ApartmentRecords { get; } = [];
    public Task<OsbbRequests.Domain.Models.OwnerApartments?> GetOwnerApartmentsAsync(long userId, CancellationToken ct) =>
        Task.FromResult(ApartmentRecords.GetValueOrDefault(userId));
    public Dictionary<long, ResidentProfileDialog> ProfileDialogs { get; } = [];
    public Task<ResidentProfileDialog?> GetProfileDialogAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(ProfileDialogs.GetValueOrDefault(userId));
    public Dictionary<long, ResidentRegistration> Registrations { get; } = [];
    public Task<bool> ResidentPhoneExistsAsync(string complexId, string phone, CancellationToken cancellationToken) =>
        Task.FromResult(Registrations.Values.Any(r => r.BuildingId == complexId && r.Stage == RegistrationStage.Approved && !r.PersonalDataDeleted && r.Phone == phone));
    public Task<ResidentRegistration?> GetRegistrationAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(Registrations.GetValueOrDefault(userId));
    public int CommitCount { get; private set; }
    public int Queries { get; private set; }
    public bool FailCommit { get; set; }

    public Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken cancellationToken) =>
        Task.FromResult(Residents.Contains(userId) && buildingId == "building");

    public Task<MaintenanceRequest?> GetRequestAsync(long userId, long requestId, CancellationToken cancellationToken) =>
        Task.FromResult(Requests.FirstOrDefault(r => r.OwnerId == userId && r.Id == requestId));

    public Task<StoredReply?> GetReplyAsync(long updateId, CancellationToken cancellationToken) =>
        Task.FromResult(Replies.GetValueOrDefault(updateId));

    public Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome, CancellationToken cancellationToken)
    {
        if (FailCommit)
            throw new InvalidOperationException("Storage unavailable.");
        if (Replies.TryGetValue(command.UpdateId, out var existing))
            return Task.FromResult(existing);
        if (outcome.Apartment is { } apartment)
        {
            if (ApartmentRecords.GetValueOrDefault(command.UserId)?.Version != apartment.ExpectedVersion ||
                Registrations.GetValueOrDefault(command.UserId)?.Version != apartment.ProfileVersion)
            {
                throw new InvalidOperationException("Apartment update conflict.");
            }

            ApartmentRecords[command.UserId] = apartment.Value;
        }
        if (outcome.PageNavigation is { } nav && PageNavigations.GetValueOrDefault(command.UserId)?.Version != nav.ExpectedVersion)
            throw new InvalidOperationException("Page navigation conflict.");
        if (outcome.Deletion is { } deletion)
        {
            var target = Requests.SingleOrDefault(r => r.OwnerId == command.UserId && r.Id == deletion.Id);
            if (deletion.OwnerId != command.UserId || !Residents.Contains(command.UserId) || target is null ||
                target.BuildingId != deletion.ComplexId || target.Status != RequestStatus.New ||
                RequestVersions.GetValueOrDefault(target.Id, 1) != deletion.ExpectedVersion)
            {
                throw new InvalidOperationException("Deletion conflict.");
            }
        }
        if (outcome.ProfileDialog is { } profileDialog && ProfileDialogs.GetValueOrDefault(command.UserId)?.Version != profileDialog.ExpectedVersion)
            throw new InvalidOperationException("Profile dialog conflict.");
        if (outcome.Profile is { } profile && (!Residents.Contains(command.UserId) || Registrations.GetValueOrDefault(command.UserId)?.Version != profile.ExpectedVersion))
            throw new InvalidOperationException("Profile update conflict.");
        if (outcome.PageNavigation is { } navChange) PageNavigations[command.UserId] = navChange.Value;
        if (outcome.ProfileDialog is { } state) ProfileDialogs[command.UserId] = state.Value;
        if (outcome.Profile is { } changed)
        {
            Registrations[command.UserId] = changed.Value;
            if (changed.Value.PersonalDataDeleted)
            {
                Residents.Remove(command.UserId);
                ApartmentRecords.Remove(command.UserId);
            }
        }
        if (outcome.Registration is { } change)
        {
            if (Registrations.GetValueOrDefault(command.UserId)?.Version != change.ExpectedVersion)
                throw new InvalidOperationException("Registration version conflict.");
            Registrations[command.UserId] = change.Value;
            if (change.AutomaticallyApproved) Residents.Add(command.UserId);
        }
        if (outcome.Draft is { } draft)
        {
            if (Drafts.GetValueOrDefault(command.UserId)?.Version != draft.ExpectedVersion)
                throw new InvalidOperationException("Draft version conflict.");
            Drafts[command.UserId] = draft.Value;
        }
        if (outcome.Lookup is { } lookup)
        {
            if (Lookups.GetValueOrDefault(command.UserId)?.Version != lookup.ExpectedVersion)
                throw new InvalidOperationException("Lookup version conflict.");
            Lookups[command.UserId] = lookup.Value;
        }
        if (outcome.Deletion is { } removed)
            Requests.RemoveAll(r => r.Id == removed.Id && r.OwnerId == removed.OwnerId);
        CommitCount++;
        if (outcome.NewRequest is { } request)
            Requests.Add(request);
        var reply = new StoredReply(command.UpdateId, command.UserId, command.ChatId, outcome.Reply, false, outcome.Keyboard, outcome.Profile?.Value.PersonalDataDeleted == true || outcome.Registration?.AutomaticallyApproved == true);
        Replies.Add(command.UpdateId, reply);
        return Task.FromResult(reply);
    }

    public Task MarkDeliveredAsync(long updateId, CancellationToken cancellationToken)
    {
        Replies[updateId] = Replies[updateId] with { Delivered = true };
        return Task.CompletedTask;
    }
}

internal sealed class FakeTelegram : ITelegramClient
{
    public List<(long ChatId, bool Registered)> Menus { get; } = [];
    public Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken, bool force = false)
    { Menus.Add((chatId, registered)); return Task.CompletedTask; }
    public List<(long ChatId, string Text)> Messages { get; } = [];
    public List<ReplyKeyboard?> Keyboards { get; } = [];
    public bool Fail { get; set; }
    public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
    {
        if (Fail)
            throw new InvalidOperationException("Delivery unavailable.");
        Messages.Add((chatId, text));
        Keyboards.Add(keyboard);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSecret(string secret) : ISecretProvider
{
    public int Reads { get; private set; }
    public Task<string> GetAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(secret);
    }
}
