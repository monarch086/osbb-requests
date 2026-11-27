using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore(IDbContextFactory<DataContext> factory, PostgresOptions options)
    : IAdministratorAccounts, IAdministratorStore, IRequestStore, IResidentStore, IMessageStore, IStatisticsReader
{
    private const int PageSize = 25;
    private sealed record Scope(long SourceId, long ComplexId);
    private Scope? scope;

    private async Task<Scope> GetScopeAsync(DataContext db, CancellationToken ct)
    {
        if (scope is not null) return scope;
        var matches = await db.Complexes.AsNoTracking().Where(complex => complex.ExternalId == options.Complex)
            .Select(complex => new Scope(complex.SourceId, complex.Id)).Take(2).ToListAsync(ct);
        if (matches.Count != 1) throw Error(AdminFailure.Unavailable, "Оберіть одне джерело даних PostgreSQL для комплексу.");
        return scope = matches[0];
    }

    private async Task<T> Read<T>(Func<DataContext, Scope, Task<T>> operation, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await operation(db, await GetScopeAsync(db, ct));
    }

    private async Task<T> Write<T>(Func<DataContext, Scope, Task<T>> operation, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var result = await operation(db, await GetScopeAsync(db, ct));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception exception) when (IsConcurrencyFailure(exception))
        {
            throw Error(AdminFailure.Conflict, "Дані або права доступу змінилися. Оновіть сторінку.");
        }
    }

    private async Task Write(Func<DataContext, Scope, Task> operation, CancellationToken ct) =>
        await Write(async (db, currentScope) => { await operation(db, currentScope); return true; }, ct);

    private static bool IsConcurrencyFailure(Exception error) => error is DbUpdateConcurrencyException ||
        (error as PostgresException ?? error.InnerException as PostgresException)?.SqlState is
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation;

    private async Task CheckActor(DataContext db, Scope currentScope, AdminUser actor, CancellationToken ct)
    {
        var current = await ReadAdminAsync(db, currentScope, actor.UserId, ct);
        if (current?.IsActive != true || current.Version != actor.Version || current.IsSuperAdmin != actor.IsSuperAdmin)
            throw Error(AdminFailure.Conflict, "Дані або права доступу змінилися. Оновіть сторінку.");
    }

    private static AdminOperationException Error(AdminFailure failure, string message) => new(failure, message);

    private static int Offset(string? cursor) => cursor is null or "" ? 0 :
        int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset : throw Error(AdminFailure.InvalidInput, "Некоректна сторінка.");

    private static string? NextCursor(int offset, int count) => count > PageSize
        ? checked(offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    private static int PageNumber(int page, int total)
    {
        if (page is < 1 or > 1000000) throw Error(AdminFailure.InvalidInput, "Некоректна сторінка.");
        return Math.Min(page, Math.Max(1, (total + PageSize - 1) / PageSize));
    }

    private void Enqueue(DataContext db, Scope currentScope, ResidentNotification? notification)
    {
        if (notification is null) return;
        db.Notifications.Add(Notification.CreatePending(currentScope.SourceId, currentScope.ComplexId, options.Complex, notification));
    }

    private static string EventKey() => "LOCAL#" + Guid.NewGuid().ToString("N");
}
