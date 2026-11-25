using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Services;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task DecideAsync(AdminUser actor, string reference, RegistrationDecision decision, CancellationToken ct) => Write(async (db, current) =>
    {
        ResidentService.RequireRegistrationReview(actor);
        await CheckActor(db, current, actor, ct);
        var row = await FindResidentForWriteAsync(db, current, reference, ct);
        if (row.Stage != RegistrationStage.Pending || row.Version != decision.Version)
            throw Error(AdminFailure.Conflict, "Рішення вже змінено. Оновіть список.");
        if (!decision.Approved && string.IsNullOrWhiteSpace(decision.Reason)) throw Error(AdminFailure.InvalidInput, "Вкажіть причину відхилення.");
        if (decision.Reason?.Length > 500) throw Error(AdminFailure.InvalidInput, "Пояснення: до 500 символів.");
        var now = DateTimeOffset.UtcNow;
        row.Stage = decision.Approved ? RegistrationStage.Approved : RegistrationStage.Rejected;
        row.DecisionReason = decision.Reason?.Trim();
        row.UpdatedAt = now;
        row.ApprovedAt = decision.Approved ? now : null;
        row.Version++;
        var member = await db.ResidentMemberships.FirstOrDefaultAsync(m => m.SourceId == current.SourceId &&
            m.ComplexId == current.ComplexId && m.TelegramUserId == row.TelegramUserId, ct);
        if (member is null)
        {
            member = new() { SourceId = current.SourceId, Key = EventKey(), ComplexId = current.ComplexId, TelegramUserId = row.TelegramUserId };
            db.ResidentMemberships.Add(member);
        }
        member.Active = decision.Approved;
        member.RegistrationId = reference;
        member.ResidentId = row.Id;
        member.ReviewedBy = actor.Email;
        member.ReviewedAt = now;
        db.ResidentEvents.Add(new()
        {
            SourceId = current.SourceId,
            Key = EventKey(),
            RegistrationId = reference,
            EventKey = "EVENT#DECIDED",
            Actor = actor.Email,
            CreatedAt = now,
            Data = JsonSerializer.Serialize(new { actor = actor.Email, createdAt = now, decision = row.Stage.ToString(), reason = row.DecisionReason })
        });
        if (decision.Approved)
            Enqueue(db, current, ResidentNotification.RegistrationApproved((await ToProfilesAsync(db, [row], ct))[0]));
    }, ct);

    public Task ChangeResidentTypeAsync(AdminUser actor, string reference, ResidentTypeChangeRequest change, CancellationToken ct) =>
        ChangeResidentAsync(actor, reference, change.ResidentType, change.Version, null, null, ct);

    public Task UpdateResidentDataAsync(AdminUser actor, string reference, ResidentDataChangeRequest change, CancellationToken ct)
    {
        if (!ValidName(change.FirstName) || !ValidName(change.LastName))
            throw Error(AdminFailure.InvalidInput, "Вкажіть коректні ім’я та прізвище.");
        return ChangeResidentAsync(actor, reference, change.ResidentType, change.Version, change.FirstName.Trim(), change.LastName.Trim(), ct);
    }

    private Task ChangeResidentAsync(AdminUser actor, string reference, string type, int version, string? first, string? last, CancellationToken ct) =>
        Write(async (db, current) =>
        {
            ResidentService.RequireRegistrationReview(actor);
            await CheckActor(db, current, actor, ct);
            if (!Enum.GetNames<ResidentType>().Contains(type)) throw Error(AdminFailure.InvalidInput, "Оберіть коректний тип мешканця.");
            var row = await FindResidentForWriteAsync(db, current, reference, ct);
            if (row.Version != version || row.Stage != RegistrationStage.Pending &&
                (first is null || row.Stage != RegistrationStage.Approved || row.PersonalDataDeleted))
                throw Error(AdminFailure.Conflict, "Дані мешканця змінено. Оновіть список.");
            var nextType = Enum.Parse<ResidentType>(type);
            var firstChanged = first is not null && first != row.FirstName;
            var lastChanged = last is not null && last != row.LastName;
            if (!firstChanged && !lastChanged && row.ResidentType == nextType)
                throw Error(AdminFailure.InvalidInput, "Дані не змінено.");
            var change = new ResidentTypeChange(row.ResidentType.ToString(), type, actor.Email, DateTimeOffset.UtcNow, firstChanged, lastChanged);
            row.ResidentType = nextType;
            row.FirstName = first ?? row.FirstName;
            row.LastName = last ?? row.LastName;
            row.TypeChangeCount++;
            row.Version++;
            AddResidentHistory(db, current, row, change);
        }, ct);

    private static async Task<Resident> FindResidentForWriteAsync(DataContext db, Scope current, string reference, CancellationToken ct) =>
        await db.Residents.SingleOrDefaultAsync(row => row.ComplexId == current.ComplexId && row.RegistrationId == reference, ct) ??
        throw Error(AdminFailure.NotFound, "Реєстрацію не знайдено.");

    private static bool ValidName(string? value)
    {
        var name = value?.Trim() ?? "";
        return name.Length is >= 1 and <= 80 && name.Any(char.IsLetter) &&
            name.All(c => char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark ||
                c is ' ' or '-' or '\'' or '’' or 'ʼ');
    }

    private static void AddResidentHistory(DataContext db, Scope area, Resident row, ResidentTypeChange change)
    {
        var key = EventKey();
        var details = change.KeyChange;
        db.ResidentEvents.Add(new()
        {
            SourceId = area.SourceId,
            RegistrationId = row.RegistrationId!,
            Key = key,
            EventKey = "EVENT#TYPE#" + key,
            Actor = change.Actor,
            CreatedAt = change.CreatedAt,
            Data = JsonSerializer.Serialize(change),
            KeyRequestId = details?.RequestId,
            PreviousKeyType = details?.Previous.Type,
            PreviousKeySerialNumber = details?.Previous.SerialNumber,
            CurrentKeyType = details?.Current?.Type,
            CurrentKeySerialNumber = details?.Current?.SerialNumber
        });
    }
}
