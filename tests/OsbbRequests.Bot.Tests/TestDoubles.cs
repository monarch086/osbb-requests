using OsbbRequests.Shared.Models;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Bot.Webhook;

namespace OsbbRequests.Bot.Tests;

internal sealed class MemoryRepository : ICommandRepository
{
    public HashSet<long> Residents { get; } = [42];
    public Dictionary<long, StoredReply> Replies { get; } = [];
    public List<MaintenanceRequest> Requests { get; } = [];
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
        if (outcome.Registration is { } change)
        {
            if (Registrations.GetValueOrDefault(command.UserId)?.Version != change.ExpectedVersion)
                throw new InvalidOperationException("Registration version conflict.");
            Registrations[command.UserId] = change.Value;
        }
        CommitCount++;
        if (outcome.NewRequest is { } request)
            Requests.Add(request);
        var reply = new StoredReply(command.UpdateId, command.UserId, command.ChatId, outcome.Reply, false, outcome.Keyboard);
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
