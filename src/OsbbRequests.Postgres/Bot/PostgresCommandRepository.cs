using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Persistence;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain.Models;

namespace OsbbRequests.Postgres.Bot;

public sealed partial class PostgresCommandRepository : ICommandRepository
{
    private const string Draft = "draft";
    private const string Lookup = "lookup";
    private const string Profile = "profile";
    private const string Navigation = "navigation";
    private readonly IDbContextFactory<DataContext> factory;
    private readonly long complexId;
    private readonly long sourceId;
    private readonly string complex;
    private readonly string bot;
    private readonly TimeProvider clock;

    private PostgresCommandRepository(IDbContextFactory<DataContext> factory, Complex scope, string bot, TimeProvider clock)
    {
        this.factory = factory;
        complexId = scope.Id;
        sourceId = scope.SourceId;
        complex = scope.ExternalId;
        this.bot = bot.ToLowerInvariant();
        this.clock = clock;
    }

    public static async Task<PostgresCommandRepository> ConnectAsync(IDbContextFactory<DataContext> factory, string complex, string bot,
        CancellationToken ct, long? postgresComplexId = null, TimeProvider? clock = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var matches = await db.Complexes.AsNoTracking().Where(row => row.ExternalId == complex &&
            (postgresComplexId == null || row.Id == postgresComplexId)).Take(2).ToListAsync(ct);
        if (matches.Count != 1)
            throw new InvalidOperationException("Expected one PostgreSQL complex.");
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new InvalidOperationException("Apply the PostgreSQL migrations before starting the bot.");
        return new(factory, matches[0], bot, clock ?? TimeProvider.System);
    }

    public Task<RequestDraft?> GetRequestDraftAsync(long userId, CancellationToken ct) => ReadStateAsync<RequestDraft>(userId, Draft, ct);
    public Task<RequestLookup?> GetRequestLookupAsync(long userId, CancellationToken ct) => ReadStateAsync<RequestLookup>(userId, Lookup, ct);
    public Task<ResidentProfileDialog?> GetProfileDialogAsync(long userId, CancellationToken ct) => ReadStateAsync<ResidentProfileDialog>(userId, Profile, ct);
    public Task<PageNavigation?> GetPageNavigationAsync(long userId, CancellationToken ct) => ReadStateAsync<PageNavigation>(userId, Navigation, ct);

    public Task<string?> GetComplexCursorAsync(long userId, string complexId, string token, CancellationToken ct) =>
        complex == complexId && ComplexRequestCursor.IsShortToken(token) ? ReadStateAsync<string>(userId, Cursor(token), ct) : Task.FromResult<string?>(null);

    private async Task<T?> ReadStateAsync<T>(long userId, string kind, CancellationToken ct) where T : class
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = await db.BotConversations.FindAsync([complexId, bot, userId, kind], ct);
        return state is not null && (state.ExpiresAt is null || state.ExpiresAt > clock.GetUtcNow())
            ? JsonSerializer.Deserialize<T>(state.Data) : null;
    }

    public async Task<StoredReply?> GetReplyAsync(long updateId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await FindReplyAsync(db, updateId, ct);
        return row is null ? null : ToReply(row);
    }

    public async Task MarkDeliveredAsync(long updateId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.BotCommandReceipts.Where(row => row.ComplexId == complexId && row.Bot == bot && row.UpdateId == updateId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Delivered, true), ct);
    }

    public async Task<StoredReply> CommitAsync(CommandEnvelope command, CommandOutcome outcome, CancellationToken ct)
    {
        try
        {
            return await CommitTransactionAsync(command, outcome, ct);
        }
        catch (Exception error) when (IsRace(error))
        {
            if (await GetReplyAsync(command.UpdateId, ct) is { } reply) return reply;
            throw;
        }
    }

    private async Task<StoredReply> CommitTransactionAsync(CommandEnvelope command, CommandOutcome outcome, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await FindReplyAsync(db, command.UpdateId, ct) is { } existing) return ToReply(existing);

        await WriteConversationAsync(db, command, outcome, ct);
        await WriteResidentAsync(db, command, outcome, ct);
        await WriteRequestsAsync(db, command, outcome, ct);
        var row = new BotCommandReceipt
        {
            ComplexId = complexId,
            Bot = bot,
            UpdateId = command.UpdateId,
            UserId = command.UserId,
            ChatId = command.ChatId,
            Text = outcome.Reply,
            Keyboard = (int?)outcome.Keyboard,
            CreatedAt = clock.GetUtcNow(),
            RefreshMenu = outcome.Profile?.Value.PersonalDataDeleted == true || outcome.Registration?.AutomaticallyApproved == true
        };
        db.BotCommandReceipts.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToReply(row);
    }

    private async Task WriteConversationAsync(DataContext db, CommandEnvelope command, CommandOutcome outcome, CancellationToken ct)
    {
        if (outcome.Draft is { } draft)
            await WriteStateAsync(db, command, draft.Value.UserId, draft.Value.ComplexId, Draft, draft.Value.Version, draft.ExpectedVersion, draft.Value, ct);
        if (outcome.Lookup is { } lookup)
            await WriteStateAsync(db, command, lookup.Value.UserId, lookup.Value.ComplexId, Lookup, lookup.Value.Version, lookup.ExpectedVersion, lookup.Value, ct);
        if (outcome.ProfileDialog is { } profile)
            await WriteStateAsync(db, command, profile.Value.UserId, profile.Value.ComplexId, Profile, profile.Value.Version, profile.ExpectedVersion, profile.Value, ct);
        if (outcome.PageNavigation is { } nav)
            await WriteStateAsync(db, command, nav.Value.UserId, nav.Value.ComplexId, Navigation, nav.Value.Version, nav.ExpectedVersion, nav.Value, ct);
        if (outcome.Pagination is { } cursor)
            await WriteStateAsync(db, command, cursor.UserId, cursor.ComplexId, Cursor(cursor.Token), 1, null, cursor.Before,
                ct, clock.GetUtcNow().AddDays(30));
    }

    private async Task WriteStateAsync<T>(DataContext db, CommandEnvelope command, long user, string externalComplex,
        string kind, int version, int? expected, T value, CancellationToken ct, DateTimeOffset? expires = null)
    {
        CheckOwner(command, user, externalComplex);
        var state = await db.BotConversations.FindAsync([complexId, bot, user, kind], ct);
        if (kind.StartsWith("cursor:", StringComparison.Ordinal) &&
            state is not null && state.ExpiresAt > clock.GetUtcNow())
            throw new InvalidOperationException("Cursor token is already used.");
        if (expected is not null && expected != state?.Version)
            throw new InvalidOperationException("Bot conversation changed.");
        if (state is null)
        {
            state = new() { ComplexId = complexId, Bot = bot, UserId = user, Kind = kind, Data = "" };
            db.BotConversations.Add(state);
        }
        state.Version = version;
        state.ExpiresAt = expires;
        state.Data = JsonSerializer.Serialize(value);
    }

    private void CheckOwner(CommandEnvelope command, long user, string externalComplex)
    {
        if (command.UserId != user || complex != externalComplex)
            throw new InvalidOperationException("Bot data scope mismatch.");
    }

    private ValueTask<BotCommandReceipt?> FindReplyAsync(DataContext db, long updateId, CancellationToken ct) =>
        db.BotCommandReceipts.FindAsync([complexId, bot, updateId], ct);

    private static StoredReply ToReply(BotCommandReceipt row) =>
        new(row.UpdateId, row.UserId, row.ChatId, row.Text, row.Delivered, (ReplyKeyboard?)row.Keyboard, row.RefreshMenu);

    private static string Cursor(string token) => "cursor:" + token;

    private static bool IsRace(Exception error) => (error as PostgresException ?? error.InnerException as PostgresException)?.SqlState is
        PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected;

    private void Enqueue(DataContext db, ResidentNotification notification) => db.Notifications.Add(new()
    {
        ComplexId = complexId,
        SourceId = sourceId,
        Key = "LOCAL#" + notification.Id,
        Kind = "LOCAL_PENDING",
        Data = JsonSerializer.Serialize(notification)
    });
}
