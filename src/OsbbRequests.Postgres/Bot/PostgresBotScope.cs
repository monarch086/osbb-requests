using Microsoft.EntityFrameworkCore;
using OsbbRequests.DataImport.Storage;

namespace OsbbRequests.Postgres.Bot;

public sealed record PostgresBotScope(long SourceId, long ComplexId, string Complex, string Bot)
{
    public static async Task<PostgresBotScope> LoadAsync(IDbContextFactory<DataContext> factory, string complex, string bot,
        CancellationToken ct, long? postgresComplexId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var matches = await db.Complexes.AsNoTracking().Where(row => row.ExternalId == complex &&
            (postgresComplexId == null || row.Id == postgresComplexId)).Take(2).ToListAsync(ct);
        if (matches.Count != 1)
            throw new InvalidOperationException("Expected one PostgreSQL complex.");
        if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            throw new InvalidOperationException("Apply the PostgreSQL migrations before starting the bot.");
        var scope = matches[0];
        return new(scope.SourceId, scope.Id, scope.ExternalId, bot.ToLowerInvariant());
    }

    internal string ReplyKey(long userId, long messageId) =>
        FormattableString.Invariant($"LOCAL_MESSAGE#{ComplexId}#{Bot}#{userId}#{messageId}");
}
