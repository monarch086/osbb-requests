using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;

namespace OsbbRequests.Data;

internal sealed class ComplexDirectory(DataContext db, long sourceId)
{
    private Dictionary<string, long> ids = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken ct) =>
        ids = await db.Complexes.Where(x => x.SourceId == sourceId)
            .ToDictionaryAsync(x => x.ExternalId, x => x.Id, StringComparer.Ordinal, ct);

    public bool TryGetId(string externalId, out long id) => ids.TryGetValue(externalId, out id);

    public async Task<long> GetOrCreateIdAsync(string externalId, CancellationToken ct)
    {
        if (ids.TryGetValue(externalId, out var id)) return id;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO complexes ("SourceId", "ExternalId") VALUES ({sourceId}, {externalId})
            ON CONFLICT ("SourceId", "ExternalId") DO NOTHING;
            """, ct);
        id = await db.Complexes.Where(x => x.SourceId == sourceId && x.ExternalId == externalId)
            .Select(x => x.Id).SingleAsync(ct);
        ids.Add(externalId, id);
        return id;
    }
}
