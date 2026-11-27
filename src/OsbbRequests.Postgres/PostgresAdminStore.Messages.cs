using Microsoft.EntityFrameworkCore;
using OsbbRequests.Services;
using OsbbRequests.Services.Admin;
using OsbbRequests.Services.Notifications;
using StoredMessage = OsbbRequests.DataImport.Storage.BroadcastMessage;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task<Page<BroadcastMessage>> ListMessagesAsync(string? cursor, CancellationToken ct) => Read(async (db, current) =>
    {
        var offset = Offset(cursor);
        var rows = await db.BroadcastMessages.Where(row => row.SourceId == current.SourceId && row.ComplexId == current.ComplexId)
            .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.MessageId).Skip(offset).Take(PageSize + 1).ToListAsync(ct);
        return new Page<BroadcastMessage>(rows.Take(PageSize).Select(ToMessage).ToArray(), NextCursor(offset, rows.Count));
    }, ct);

    public async Task<BroadcastMessage> SendMessageAsync(AdminUser actor, MessageChange change, CancellationToken ct)
    {
        MessageService.RequireMessagesAccess(actor);
        if (!Guid.TryParseExact(change.Id, "D", out var id) || string.IsNullOrWhiteSpace(change.Text) || change.Text.Length > 4096)
            throw Error(AdminFailure.InvalidInput, "Вкажіть текст повідомлення від 1 до 4096 символів і коректний ідентифікатор.");
        var messageId = id.ToString("D");
        var text = change.Text.Trim();
        StoredMessage? Match(StoredMessage? row) => row is null || row.CreatedById == actor.UserId && row.Text == text ? row :
            throw Error(AdminFailure.Conflict, "Цей ідентифікатор вже використаний для іншого повідомлення.");
        try
        {
            return await Write(async (db, current) =>
            {
                await CheckActor(db, current, actor, ct);
                if (Match(await FindMessage(db, current, messageId, ct)) is { } existing) return ToMessage(existing);
                var row = new StoredMessage
                {
                    SourceId = current.SourceId,
                    ComplexId = current.ComplexId,
                    Key = $"LOCAL#{current.ComplexId}#{messageId}",
                    MessageId = messageId,
                    Text = text,
                    CreatedById = actor.UserId,
                    CreatedBy = actor.Email,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.BroadcastMessages.Add(row);
                Enqueue(db, current, new ResidentNotification("message-" + messageId, options.Complex, 0, null, null, text, row.CreatedAt,
                    ResidentBroadcastId: messageId));
                return ToMessage(row);
            }, ct);
        }
        catch (AdminOperationException error) when (error.Failure == AdminFailure.Conflict)
        {
            var row = await Read(async (db, current) =>
            {
                await CheckActor(db, current, actor, ct);
                return await FindMessage(db, current, messageId, ct);
            }, ct);
            if (Match(row) is { } existing) return ToMessage(existing);
            throw;
        }
    }

    private static Task<StoredMessage?> FindMessage(OsbbRequests.DataImport.Storage.DataContext db, Scope current, string id, CancellationToken ct) =>
        db.BroadcastMessages.Where(row => row.SourceId == current.SourceId && row.ComplexId == current.ComplexId && row.MessageId == id)
            .OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(ct);

    private static BroadcastMessage ToMessage(StoredMessage row) => new(row.MessageId, row.Text, row.CreatedBy, row.CreatedById, row.CreatedAt);

    public Task<IReadOnlyList<DailyStatistic>> ReadAsync(int days, CancellationToken ct) => Read<IReadOnlyList<DailyStatistic>>(async (db, current) =>
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddDays(1 - days);
        var rows = await db.ApiDailyStatistics.Where(row => row.SourceId == current.SourceId && row.Date >= start && row.Date <= today)
            .OrderBy(row => row.Date).ThenBy(row => row.Endpoint).ToListAsync(ct);
        return rows.Select(row => new DailyStatistic(row.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            row.Endpoint, row.Requests, row.ReadUnits, row.WriteUnits, row.DurationMs)).ToArray();
    }, ct);
}
