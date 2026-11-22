using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Data;

internal sealed class OwnerApartmentProjector(DataContext db)
{
    public static bool TryParseKey(string pk, out string complexId, out long ownerId)
    {
        const string prefix = "COMPLEX#";
        const string separator = "#OWNER_APARTMENTS#";
        var marker = pk.LastIndexOf(separator, StringComparison.Ordinal);
        complexId = "";
        ownerId = 0;
        if (!pk.StartsWith(prefix, StringComparison.Ordinal) || marker <= prefix.Length ||
            !long.TryParse(pk.AsSpan(marker + separator.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ownerId) || ownerId <= 0)
            return false;
        complexId = pk[prefix.Length..marker];
        return pk == OwnerApartments.Key(complexId, ownerId);
    }

    public async Task<bool> ProjectAsync(string source, string complexId, long ownerId, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var record = OwnerApartments.Read(item, complexId, ownerId)
            ?? throw new InvalidOperationException("Missing owner apartment payload.");
        var current = await Rows(source, complexId, ownerId).ToDictionaryAsync(row => row.ApartmentId, ct);
        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var apartment in record.Items)
        {
            if (string.IsNullOrEmpty(record.RegistrationId) || string.IsNullOrEmpty(apartment.Id) || !seen.Add(apartment.Id) ||
                !ComplexLayout.BuildingNumbers.Contains(apartment.BuildingNumber, StringComparer.Ordinal) ||
                apartment.ApartmentNumber is < 1 or > 999 || !Enum.IsDefined(apartment.Status))
                throw new InvalidOperationException("Invalid owner apartment projection.");
            var next = new ResidentApartment
            {
                Source = source,
                ComplexId = complexId,
                TelegramUserId = ownerId,
                ApartmentId = apartment.Id,
                RegistrationId = record.RegistrationId,
                BuildingNumber = apartment.BuildingNumber,
                ApartmentNumber = apartment.ApartmentNumber,
                Status = apartment.Status,
                SubmittedAt = ToDatabaseTimestamp(apartment.SubmittedAt),
                ReviewedAt = apartment.ReviewedAt is { } reviewedAt ? ToDatabaseTimestamp(reviewedAt) : null,
                ReviewedBy = apartment.ReviewedBy
            };
            if (current.Remove(apartment.Id, out var row))
            {
                var entry = db.Entry(row);
                entry.CurrentValues.SetValues(next);
                changed |= entry.State == EntityState.Modified;
            }
            else
            {
                db.ResidentApartments.Add(next);
                changed = true;
            }
        }
        db.ResidentApartments.RemoveRange(current.Values);
        return changed || current.Count > 0;
    }

    public Task<int> RemoveAsync(string source, string complexId, long ownerId, CancellationToken ct) =>
        Rows(source, complexId, ownerId).ExecuteDeleteAsync(ct);

    // PostgreSQL stores timestamps at microsecond precision.
    private static DateTimeOffset ToDatabaseTimestamp(DateTimeOffset timestamp) =>
        new(timestamp.UtcTicks - timestamp.UtcTicks % 10, TimeSpan.Zero);

    private IQueryable<ResidentApartment> Rows(string source, string complexId, long ownerId) =>
        db.ResidentApartments.Where(row => row.Source == source && row.ComplexId == complexId && row.TelegramUserId == ownerId);
}
