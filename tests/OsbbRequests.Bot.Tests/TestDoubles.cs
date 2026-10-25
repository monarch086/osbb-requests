using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;
using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Bot.Webhook;

namespace OsbbRequests.Bot.Tests;

internal sealed class MemoryRepository : ICommandRepository
{
    public Dictionary<long, int> RequestVersions { get; } = [];
    public Task<DeletableRequest?> GetDeletableRequestAsync(long userId, long requestId, CancellationToken ct)
    {
        var request = Requests.FirstOrDefault(r => r.OwnerId == userId && r.Id == requestId);
        return Task.FromResult(request is null ? null : new DeletableRequest(request.Id, request.OwnerId,
            request.BuildingId, request.Status, RequestVersions.GetValueOrDefault(request.Id, 1)));
    }
    public Task<PublicRequestPage> ListPublicRequestsAsync(string complexId, string? before, CancellationToken ct)
    {
        Queries++;
        static string SortKey(MaintenanceRequest r) => r.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#" + r.Id.ToString(CultureInfo.InvariantCulture);
        var candidates = Requests.Where(r => r.BuildingId == complexId && (before is null || string.CompareOrdinal(SortKey(r), before) < 0))
            .OrderByDescending(SortKey, StringComparer.Ordinal).Take(6).ToList();
        var page = candidates.Take(5).ToList();
        var items = page.Where(r => r.Visibility == RequestVisibility.Public && r.Status is RequestStatus.New or RequestStatus.InProgress)
            .Select(r => new PublicRequestSummary(r.Id, r.Status, r.CreatedAt, r.Scope, r.BuildingNumber, r.Floor, r.Category, r.Location, r.Description, r.Entrance)).ToList();
        return Task.FromResult(new PublicRequestPage(items, candidates.Count > 5 ? SortKey(page[^1]) : null));
    }
    public HashSet<long> Residents { get; } = [42];
    public Dictionary<long, StoredReply> Replies { get; } = [];
    public List<MaintenanceRequest> Requests { get; } = [];
    public Dictionary<long, RequestLookup> Lookups { get; } = [];
    public Task<RequestLookup?> GetRequestLookupAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(Lookups.GetValueOrDefault(userId));
    public Dictionary<long, RequestDraft> Drafts { get; } = [];
    public Task<RequestDraft?> GetRequestDraftAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(Drafts.GetValueOrDefault(userId));
    public Dictionary<long, ResidentProfileDialog> ProfileDialogs { get; } = [];
    public Task<ResidentProfileDialog?> GetProfileDialogAsync(long userId, CancellationToken cancellationToken) => Task.FromResult(ProfileDialogs.GetValueOrDefault(userId));
    public Dictionary<long, ResidentRegistration> Registrations { get; } = [];
    public Task<ResidentRegistration?> GetRegistrationAsync(long userId, CancellationToken cancellationToken) =>
        Task.FromResult(Registrations.GetValueOrDefault(userId));
    public int CommitCount { get; private set; }
    public int Queries { get; private set; }
    public bool FailCommit { get; set; }

    public Task<bool> IsResidentAsync(long userId, string buildingId, CancellationToken cancellationToken) =>
        Task.FromResult(Residents.Contains(userId) && buildingId == "building");

    public Task<MaintenanceRequest?> GetRequestAsync(long userId, long requestId, CancellationToken cancellationToken) =>
        Task.FromResult(Requests.FirstOrDefault(r => r.OwnerId == userId && r.Id == requestId));

    public Task<RequestPage> ListRequestsAsync(long userId, long? beforeId, CancellationToken cancellationToken)
    {
        Queries++;
        var rows = Requests.Where(r => r.OwnerId == userId && (!beforeId.HasValue || r.Id < beforeId.Value))
            .OrderByDescending(r => r.Id).Take(11).ToList();
        return Task.FromResult(new RequestPage(rows.Take(10).ToList(), rows.Count > 10 ? rows[9].Id : null));
    }

    public Task<StoredReply?> GetReplyAsync(long updateId, CancellationToken cancellationToken) =>
        Task.FromResult(Replies.GetValueOrDefault(updateId));

    public Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome, CancellationToken cancellationToken)
    {
        if (FailCommit)
            throw new InvalidOperationException("Storage unavailable.");
        if (Replies.TryGetValue(command.UpdateId, out var existing))
            return Task.FromResult(existing);
        if (outcome.Deletion is { } deletion)
        {
            var target = Requests.SingleOrDefault(r => r.OwnerId == command.UserId && r.Id == deletion.Id);
            if (deletion.OwnerId != command.UserId || !Residents.Contains(command.UserId) || target is null ||
                target.BuildingId != deletion.ComplexId || target.Status != RequestStatus.New ||
                RequestVersions.GetValueOrDefault(target.Id, 1) != deletion.ExpectedVersion)
                throw new InvalidOperationException("Deletion conflict.");
        }
        if (outcome.ProfileDialog is { } profileDialog && ProfileDialogs.GetValueOrDefault(command.UserId)?.Version != profileDialog.ExpectedVersion)
            throw new InvalidOperationException("Profile dialog conflict.");
        if (outcome.Profile is { } profile && (!Residents.Contains(command.UserId) || Registrations.GetValueOrDefault(command.UserId)?.Version != profile.ExpectedVersion))
            throw new InvalidOperationException("Profile update conflict.");
        if (outcome.ProfileDialog is { } state) ProfileDialogs[command.UserId] = state.Value;
        if (outcome.Profile is { } changed)
        {
            Registrations[command.UserId] = changed.Value;
            if (changed.Value.PersonalDataDeleted) Residents.Remove(command.UserId);
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
    public bool Fail { get; set; }
    public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
    {
        if (Fail)
            throw new InvalidOperationException("Delivery unavailable.");
        Messages.Add((chatId, text));
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

internal sealed class FakeQueue : ICommandQueue
{
    public List<CommandEnvelope> Commands { get; } = [];
    public bool Fail { get; set; }
    public Task EnqueueAsync(CommandEnvelope command, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new InvalidOperationException("Queue unavailable.");
        Commands.Add(command);
        return Task.CompletedTask;
    }
}
