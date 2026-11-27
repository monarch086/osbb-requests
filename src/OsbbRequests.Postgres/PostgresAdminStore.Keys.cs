using Microsoft.EntityFrameworkCore;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Domain.Residents;
using OsbbRequests.Services;

namespace OsbbRequests.Postgres;

public sealed partial class PostgresAdminStore
{
    public Task<ResidentKeyPage> ListResidentsWithKeysAsync(int page, CancellationToken ct) => Read(async (db, area) =>
    {
        if (page is < 1 or > 1000000) throw Error(AdminFailure.InvalidInput, "Некоректна сторінка.");
        var rows = await (from order in Requests(db, area)
                          join key in db.RequestKeyItems on new { order.SourceId, order.OwnerId, order.RequestId }
                              equals new { key.SourceId, key.OwnerId, key.RequestId }
                          where order.Status == RequestStatus.Completed && order.Category == RequestCategories.KeyOrder
                          orderby order.RequestId descending, order.OwnerId, key.Position
                          select new { key.Phone, Key = new ResidentKey(key.Type, key.SerialNumber, key.RequestId, key.Position, key.OwnerId, order.Version) }).ToListAsync(ct);
        var groups = rows.GroupBy(row => KeyPhone(row.Phone), StringComparer.Ordinal).ToArray();
        var actual = PageNumber(page, groups.Length);
        var selected = groups.Skip((actual - 1) * PageSize).Take(PageSize).ToArray();
        var profiles = await KeyProfilesAsync(db, area, selected.Select(group => group.Key), ct);
        return new ResidentKeyPage(selected.Select(group => new ResidentKeyEntry(group.Key, profiles[group.Key].Select(ToKeyResident).ToArray(),
            group.Select(item => item.Key).ToArray())).ToArray(), actual, PageSize, groups.Length);
    }, ct);

    public Task UpdateResidentKeyAsync(AdminUser actor, long ownerId, long requestId, int index, ResidentKeyUpdate change, CancellationToken ct) =>
        Write(async (db, area) =>
        {
            ResidentService.RequireKeyEdit(actor);
            await CheckActor(db, area, actor, ct);
            if (change.Version is null or < 0 || change.SerialNumber is { Length: > 25 } ||
                (change.Remove ? change.Type is not null || change.SerialNumber is not null : change.Type is not ("Магніт" or "Перепустка")))
                throw Error(AdminFailure.InvalidInput, "Оберіть тип ключа та вкажіть серійний номер до 25 символів за наявності.");
            var row = await Requests(db, area).SingleOrDefaultAsync(row => row.OwnerId == ownerId && row.RequestId == requestId, ct)
                ?? throw Error(AdminFailure.NotFound, "Заявку не знайдено.");
            var keys = await KeyRows(db, row).ToListAsync(ct);
            if (row.Version != change.Version || row.Category != RequestCategories.KeyOrder || row.Status != RequestStatus.Completed ||
                index < 0 || index >= keys.Count)
                throw Error(AdminFailure.Conflict, "Замовлення або ключ змінено. Оновіть список.");
            var selected = keys[index];
            var previous = new ResidentKeyDetails(selected.Type, selected.SerialNumber);
            var next = change.Remove ? null : new ResidentKeyDetails(change.Type!, string.IsNullOrWhiteSpace(change.SerialNumber) ? null : change.SerialNumber.Trim());
            if (next == previous) throw Error(AdminFailure.InvalidInput, "Дані ключа не змінено.");
            var matches = await KeyProfilesAsync(db, area, [selected.Phone], ct);
            var profiles = matches[KeyPhone(selected.Phone)].ToArray();
            if (profiles.Length == 0) throw Error(AdminFailure.Conflict, "Профіль мешканця для цього ключа не знайдено.");
            if (next is null)
            {
                var remaining = keys.Where(key => key != selected).Select((key, position) => new RequestKeyItem
                {
                    SourceId = key.SourceId,
                    OwnerId = key.OwnerId,
                    RequestId = key.RequestId,
                    Position = position,
                    Phone = key.Phone,
                    Type = key.Type,
                    SerialNumber = key.SerialNumber
                }).ToList();
                db.RequestKeyItems.RemoveRange(keys);
                await db.SaveChangesAsync(ct);
                db.RequestKeyItems.AddRange(remaining);
                row.KeyType = remaining.FirstOrDefault()?.Type;
                row.KeyQuantity = remaining.Count;
            }
            else
            {
                selected.Type = next.Type;
                selected.SerialNumber = next.SerialNumber;
                row.KeyType = keys[0].Type;
            }
            row.Version++;
            var now = DateTimeOffset.UtcNow;
            var details = new ResidentKeyChange(requestId, previous, next);
            foreach (var profile in profiles)
            {
                profile.TypeChangeCount++;
                AddResidentHistory(db, area, profile, new(null, null, actor.Email, now, KeyChange: details));
            }
            AddRequestEvent(db, row, now, actor.Email, row.Status);
        }, ct);

    private static string KeyPhone(string phone) => PhoneNumber.TryNormalize(phone, out var normalized) ? normalized! : phone;

    private static async Task<ILookup<string, Resident>> KeyProfilesAsync(DataContext db, Scope area, IEnumerable<string> phones, CancellationToken ct)
    {
        var normalized = phones.Select(KeyPhone).Distinct().ToArray();
        var matches = await (from phone in db.ResidentPhones
                             join resident in ResidentRows(db, area, pending: false) on phone.ResidentId equals resident.Id
                             where normalized.Contains(phone.Phone)
                             orderby resident.TelegramUserId
                             select new { phone.Phone, Resident = resident }).ToListAsync(ct);
        return matches.ToLookup(row => row.Phone, row => row.Resident, StringComparer.Ordinal);
    }

    private static KeyResident ToKeyResident(Resident resident)
    {
        var name = string.Join(" ", new[] { resident.FirstName, resident.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new(name.Length > 0 ? name : "Мешканець", resident.BuildingNumber, resident.ApartmentNumber);
    }
}
