using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;

namespace OsbbRequests.Data;

internal sealed class RelatedProjector(DataContext db, ComplexDirectory complexes)
{
    public const int Version = 2;

    public async Task ProjectAsync(long sourceId, string table, string key, Dictionary<string, AttributeValue> item, string json, CancellationToken ct)
    {
        var pk = item.GetValueOrDefault("pk")?.S ?? "";
        var sk = item.GetValueOrDefault("sk")?.S ?? "";
        if (table == "administrators")
        {
            var id = item["UserId"].S;
            var admin = await db.Administrators.FindAsync([sourceId, id], ct);
            if (admin is null)
            {
                admin = new Administrator { SourceId = sourceId, UserId = id };
                db.Administrators.Add(admin);
            }
            admin.Email = item.GetValueOrDefault("Email")?.S;
            admin.DisplayName = item.GetValueOrDefault("DisplayName")?.S;
            admin.IsActive = item.GetValueOrDefault("IsActive")?.BOOL == true;
            admin.Version = int.Parse(item.GetValueOrDefault("Version")?.N ?? "0", CultureInfo.InvariantCulture);
            admin.CreatedAt = Date(item.GetValueOrDefault("CreatedAt")?.S);
            admin.UpdatedAt = Date(item.GetValueOrDefault("UpdatedAt")?.S);

            var permissions = await db.AdministratorPermissions.FindAsync([sourceId, id], ct);
            if (permissions is null) { permissions = new AdministratorPermission { SourceId = sourceId, UserId = id }; db.AdministratorPermissions.Add(permissions); }
            permissions.ComplexRequests = item.GetValueOrDefault("AccessComplexRequests")?.BOOL ?? true;
            permissions.KeyOrders = item.GetValueOrDefault("AccessKeyOrders")?.BOOL ?? true;
            permissions.RegisteredResidents = item.GetValueOrDefault("AccessRegisteredResidents")?.BOOL ?? true;
            permissions.PendingResidents = item.GetValueOrDefault("AccessPendingResidents")?.BOOL ?? true;
            permissions.Messages = item.GetValueOrDefault("AccessMessages")?.BOOL ?? false;
            var settings = await db.AdministratorNotificationSettings.FindAsync([sourceId, id], ct);
            if (settings is null) { settings = new AdministratorNotificationSetting { SourceId = sourceId, UserId = id }; db.AdministratorNotificationSettings.Add(settings); }
            settings.TelegramChatId = item.GetValueOrDefault("TelegramChatId")?.S;
            settings.NewResidents = item.GetValueOrDefault("NotifyNewResidents")?.BOOL ?? false;
            settings.NewApartmentRegistrations = item.GetValueOrDefault("NotifyNewApartmentRegistrations")?.BOOL ?? settings.NewResidents;
            settings.NewRequests = item.GetValueOrDefault("NotifyNewRequests")?.BOOL ?? false;
            settings.NewKeyOrders = item.GetValueOrDefault("NotifyNewKeyOrders")?.BOOL ?? settings.NewRequests;
            settings.NewAdministrators = item.GetValueOrDefault("NotifyNewAdministrators")?.BOOL ?? false;
        }
        if (table == "command-data" && pk.StartsWith("USER#", StringComparison.Ordinal) && sk.StartsWith("REQUEST#", StringComparison.Ordinal))
        {
            var owner = long.Parse(pk[5..], CultureInfo.InvariantCulture);
            var requestId = long.Parse(sk[8..], CultureInfo.InvariantCulture);
            await db.RequestKeyItems.Where(x => x.SourceId == sourceId && x.OwnerId == owner && x.RequestId == requestId).ExecuteDeleteAsync(ct);
            using var payload = JsonDocument.Parse(item["payload"].S);
            if (payload.RootElement.TryGetProperty("KeyItems", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                var position = 0;
                foreach (var value in keys.EnumerateArray())
                {
                    db.RequestKeyItems.Add(new RequestKeyItem
                    {
                        SourceId = sourceId,
                        OwnerId = owner,
                        RequestId = requestId,
                        Position = position++,
                        Type = String(value, "Type") ?? "",
                        Phone = String(value, "Phone") ?? "",
                        SerialNumber = String(value, "SerialNumber")
                    });
                }
            }
        }
        if (table == "command-data" && pk.StartsWith("REQUEST#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal) && long.TryParse(pk[8..], out var request))
        {
            var row = await db.RequestEvents.FindAsync([sourceId, key], ct);
            if (row is null) db.RequestEvents.Add(new RequestEvent { SourceId = sourceId, Key = key, RequestId = request, EventKey = sk, CreatedAt = Date(item.GetValueOrDefault("createdAt")?.S), Data = json });
            else { row.Data = json; row.CreatedAt = Date(item.GetValueOrDefault("createdAt")?.S); }
        }
        if (table == "command-data" && pk.StartsWith("MESSAGES#", StringComparison.Ordinal) && sk.StartsWith("MESSAGE#", StringComparison.Ordinal))
        {
            using var payload = JsonDocument.Parse(item["payload"].S);
            var data = payload.RootElement;
            var row = await db.BroadcastMessages.FindAsync([sourceId, key], ct);
            if (row is null)
            {
                row = new BroadcastMessage { SourceId = sourceId, Key = key, ComplexId = await complexes.GetOrCreateIdAsync(pk[9..], ct), MessageId = String(data, "Id") ?? "", Text = "", CreatedById = "", CreatedBy = "" };
                db.BroadcastMessages.Add(row);
            }
            row.Text = String(data, "Text") ?? "";
            row.CreatedBy = String(data, "CreatedBy") ?? "";
            row.CreatedById = String(data, "CreatedById") ?? "";
            row.CreatedAt = Date(String(data, "CreatedAt")) ?? throw new InvalidOperationException("Invalid message date.");
        }
        if (table == "command-data" && pk.StartsWith("NOTIFICATIONS#", StringComparison.Ordinal))
        {
            var row = await db.Notifications.FindAsync([sourceId, key], ct);
            if (row is null) db.Notifications.Add(new Notification { SourceId = sourceId, Key = key, ComplexId = await complexes.GetOrCreateIdAsync(pk[14..], ct), Kind = sk.Split('#', 2)[0], Data = json });
            else row.Data = json;
        }
        if (table == "command-data" && pk.StartsWith("ADMIN#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
        {
            var row = await db.AdministratorEvents.FindAsync([sourceId, key], ct);
            if (row is null) db.AdministratorEvents.Add(new AdministratorEvent { SourceId = sourceId, Key = key, AdministratorId = pk[6..], Data = json });
            else row.Data = json;
        }
        if (table == "residents") await ProjectResidentAsync(sourceId, key, item, ct);
        if (table == "statistics" && pk == "API" && TryParseStatisticKey(sk, out var date, out var endpoint))
        {
            var row = await db.ApiDailyStatistics.FindAsync([sourceId, date, endpoint], ct);
            if (row is null) { row = new ApiDailyStatistic { SourceId = sourceId, Date = date, Endpoint = endpoint }; db.ApiDailyStatistics.Add(row); }
            row.Requests = (long)Number(item, "requests"); row.ReadUnits = Number(item, "readUnits");
            row.WriteUnits = Number(item, "writeUnits"); row.DurationMs = Number(item, "durationMs");
        }
    }

    private async Task ProjectResidentAsync(long sourceId, string key, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var sourcePk = item.GetValueOrDefault("sourcePk")?.S ?? "";
        var sourceSk = item.GetValueOrDefault("sourceSk")?.S ?? "";
        var data = item.GetValueOrDefault("recordData")?.M;
        if (sourcePk.StartsWith("REGISTRATION#", StringComparison.Ordinal) && sourceSk.StartsWith("EVENT#", StringComparison.Ordinal) && data is { Count: > 0 })
        {
            var value = Document.FromAttributeMap(data).ToJson();
            var row = await db.ResidentEvents.FindAsync([sourceId, key], ct);
            if (row is null) db.ResidentEvents.Add(new ResidentEvent { SourceId = sourceId, Key = key, RegistrationId = sourcePk[13..], EventKey = sourceSk, Data = value });
            else row.Data = value;
        }
        if (sourcePk.StartsWith("USER#", StringComparison.Ordinal) && sourceSk == "MEMBERSHIP" && data is { Count: > 0 } && long.TryParse(sourcePk[5..], out var id))
        {
            var membershipComplex = item.GetValueOrDefault("complexId")?.S;
            if (string.IsNullOrEmpty(membershipComplex)) membershipComplex = data.GetValueOrDefault("buildingId")?.S ?? "";
            var complexId = string.IsNullOrEmpty(membershipComplex) ? (long?)null : await complexes.GetOrCreateIdAsync(membershipComplex, ct);
            var row = await db.ResidentMemberships.FindAsync([sourceId, key], ct);
            if (row is null)
            {
                row = new ResidentMembership { SourceId = sourceId, Key = key, ComplexId = complexId, TelegramUserId = id };
                db.ResidentMemberships.Add(row);
            }
            if (row.ComplexId != complexId || row.TelegramUserId != id) row.ResidentId = null;
            row.ComplexId = complexId;
            row.TelegramUserId = id;
            row.Active = data.GetValueOrDefault("active")?.BOOL == true;
            row.RegistrationId = data.GetValueOrDefault("registrationId")?.S;
            row.ReviewedBy = data.GetValueOrDefault("reviewedBy")?.S;
            row.ReviewedAt = Date(data.GetValueOrDefault("reviewedAt")?.S);
        }
    }

    public async Task ReconcileResidentLinksAsync(long sourceId, CancellationToken ct)
    {
        // The residents scan can return apartments and memberships before their profiles.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE resident_apartments AS apartment
            SET "ResidentId" = resident."Id"
            FROM residents AS resident
            JOIN complexes AS complex ON complex."Id" = resident."ComplexId"
            WHERE complex."SourceId" = {sourceId}
              AND apartment."ComplexId" = resident."ComplexId"
              AND apartment."TelegramUserId" = resident."TelegramUserId"
              AND apartment."ResidentId" IS DISTINCT FROM resident."Id";

            UPDATE resident_memberships AS membership
            SET "ResidentId" = resident."Id"
            FROM residents AS resident
            WHERE membership."SourceId" = {sourceId}
              AND membership."ComplexId" = resident."ComplexId"
              AND membership."TelegramUserId" = resident."TelegramUserId"
              AND membership."ResidentId" IS DISTINCT FROM resident."Id";
            """, ct);
    }

    public async Task RemoveAsync(long sourceId, string table, string key, string json, CancellationToken ct)
    {
        var parts = key.Split('\u001f', 2);
        var pk = parts[0];
        var sk = parts.Length > 1 ? parts[1] : "";
        if (table == "administrators")
        {
            await db.AdministratorPermissions.Where(x => x.SourceId == sourceId && x.UserId == key).ExecuteDeleteAsync(ct);
            await db.AdministratorNotificationSettings.Where(x => x.SourceId == sourceId && x.UserId == key).ExecuteDeleteAsync(ct);
        }
        if (table == "command-data")
        {
            if (pk.StartsWith("USER#", StringComparison.Ordinal) && sk.StartsWith("REQUEST#", StringComparison.Ordinal) &&
                long.TryParse(pk[5..], out var owner) && long.TryParse(sk[8..], out var request))
                await db.RequestKeyItems.Where(x => x.SourceId == sourceId && x.OwnerId == owner && x.RequestId == request).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("REQUEST#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
                await db.RequestEvents.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("MESSAGES#", StringComparison.Ordinal) && sk.StartsWith("MESSAGE#", StringComparison.Ordinal))
                await db.BroadcastMessages.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("NOTIFICATIONS#", StringComparison.Ordinal))
                await db.Notifications.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("ADMIN#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
                await db.AdministratorEvents.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        }
        if (table == "statistics" && pk == "API" && TryParseStatisticKey(sk, out var date, out var endpoint))
            await db.ApiDailyStatistics.Where(x => x.SourceId == sourceId && x.Date == date && x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
        if (table != "residents") return;
        using var record = JsonDocument.Parse(json);
        var sourcePk = String(record.RootElement, "sourcePk") ?? "";
        var sourceSk = String(record.RootElement, "sourceSk") ?? "";
        if (sourcePk.StartsWith("REGISTRATION#", StringComparison.Ordinal) && sourceSk.StartsWith("EVENT#", StringComparison.Ordinal))
            await db.ResidentEvents.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        if (sourcePk.StartsWith("USER#", StringComparison.Ordinal) && sourceSk == "MEMBERSHIP")
            await db.ResidentMemberships.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
    }
    private static bool TryParseStatisticKey(string key, out DateOnly date, out string endpoint)
    {
        date = default;
        endpoint = "";
        if (key.Length <= 11 || key[10] != '#' ||
            !DateOnly.TryParseExact(key.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        endpoint = key[11..];
        return true;
    }

    private static string? String(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
    private static double Number(Dictionary<string, AttributeValue> item, string key) => double.TryParse(item.GetValueOrDefault(key)?.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
