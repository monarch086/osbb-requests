using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;

namespace OsbbRequests.Data;

public sealed class RelatedProjector(DataContext db)
{
    public const int Version = 2;

    public async Task ProjectAsync(string source, string table, string key, Dictionary<string, AttributeValue> item, string json, CancellationToken ct)
    {
        var pk = item.GetValueOrDefault("pk")?.S ?? "";
        var sk = item.GetValueOrDefault("sk")?.S ?? "";
        if (table == "administrators")
        {
            var id = item["UserId"].S;
            var admin = await db.Administrators.FindAsync([source, id], ct);
            if (admin is null)
            {
                admin = new Administrator { Source = source, UserId = id };
                db.Administrators.Add(admin);
            }
            admin.Email = item.GetValueOrDefault("Email")?.S;
            admin.DisplayName = item.GetValueOrDefault("DisplayName")?.S;
            admin.IsActive = item.GetValueOrDefault("IsActive")?.BOOL == true;
            admin.Version = int.Parse(item.GetValueOrDefault("Version")?.N ?? "0", CultureInfo.InvariantCulture);
            admin.CreatedAt = Date(item.GetValueOrDefault("CreatedAt")?.S);
            admin.UpdatedAt = Date(item.GetValueOrDefault("UpdatedAt")?.S);

            var permissions = await db.AdministratorPermissions.FindAsync([source, id], ct);
            if (permissions is null) { permissions = new AdministratorPermission { Source = source, UserId = id }; db.AdministratorPermissions.Add(permissions); }
            permissions.ComplexRequests = item.GetValueOrDefault("AccessComplexRequests")?.BOOL ?? true;
            permissions.KeyOrders = item.GetValueOrDefault("AccessKeyOrders")?.BOOL ?? true;
            permissions.RegisteredResidents = item.GetValueOrDefault("AccessRegisteredResidents")?.BOOL ?? true;
            permissions.PendingResidents = item.GetValueOrDefault("AccessPendingResidents")?.BOOL ?? true;
            permissions.Messages = item.GetValueOrDefault("AccessMessages")?.BOOL ?? false;
            var settings = await db.AdministratorNotificationSettings.FindAsync([source, id], ct);
            if (settings is null) { settings = new AdministratorNotificationSetting { Source = source, UserId = id }; db.AdministratorNotificationSettings.Add(settings); }
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
            await db.RequestKeyItems.Where(x => x.Source == source && x.OwnerId == owner && x.RequestId == requestId).ExecuteDeleteAsync(ct);
            using var payload = JsonDocument.Parse(item["payload"].S);
            if (payload.RootElement.TryGetProperty("KeyItems", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                var position = 0;
                foreach (var value in keys.EnumerateArray())
                {
                    db.RequestKeyItems.Add(new RequestKeyItem
                    {
                        Source = source,
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
            var row = await db.RequestEvents.FindAsync([source, key], ct);
            if (row is null) db.RequestEvents.Add(new RequestEvent { Source = source, Key = key, RequestId = request, EventKey = sk, CreatedAt = Date(item.GetValueOrDefault("createdAt")?.S), Data = json });
            else { row.Data = json; row.CreatedAt = Date(item.GetValueOrDefault("createdAt")?.S); }
        }
        if (table == "command-data" && pk.StartsWith("MESSAGES#", StringComparison.Ordinal) && sk.StartsWith("MESSAGE#", StringComparison.Ordinal))
        {
            using var payload = JsonDocument.Parse(item["payload"].S);
            var data = payload.RootElement;
            var row = await db.BroadcastMessages.FindAsync([source, key], ct);
            if (row is null)
            {
                row = new BroadcastMessage { Source = source, Key = key, ComplexId = pk[9..], MessageId = String(data, "Id") ?? "", Text = "", CreatedById = "", CreatedBy = "" };
                db.BroadcastMessages.Add(row);
            }
            row.Text = String(data, "Text") ?? "";
            row.CreatedBy = String(data, "CreatedBy") ?? "";
            row.CreatedById = String(data, "CreatedById") ?? "";
            row.CreatedAt = Date(String(data, "CreatedAt")) ?? throw new InvalidOperationException("Invalid message date.");
        }
        if (table == "command-data" && pk.StartsWith("NOTIFICATIONS#", StringComparison.Ordinal))
        {
            var row = await db.Notifications.FindAsync([source, key], ct);
            if (row is null) db.Notifications.Add(new Notification { Source = source, Key = key, ComplexId = pk[14..], Kind = sk.Split('#', 2)[0], Data = json });
            else row.Data = json;
        }
        if (table == "command-data" && pk.StartsWith("ADMIN#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
        {
            var row = await db.AdministratorEvents.FindAsync([source, key], ct);
            if (row is null) db.AdministratorEvents.Add(new AdministratorEvent { Source = source, Key = key, AdministratorId = pk[6..], Data = json });
            else row.Data = json;
        }
        if (table == "residents") await ProjectResidentAsync(source, key, item, ct);
        if (table == "statistics" && pk == "API" && sk.Length > 11 && sk[10] == '#')
        {
            var row = await db.ApiDailyStatistics.FindAsync([source, key], ct);
            if (row is null) { row = new ApiDailyStatistic { Source = source, Key = key, Date = sk[..10], Endpoint = sk[11..] }; db.ApiDailyStatistics.Add(row); }
            row.Requests = (long)Number(item, "requests"); row.ReadUnits = Number(item, "readUnits");
            row.WriteUnits = Number(item, "writeUnits"); row.DurationMs = Number(item, "durationMs");
        }
    }

    private async Task ProjectResidentAsync(string source, string key, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var sourcePk = item.GetValueOrDefault("sourcePk")?.S ?? "";
        var sourceSk = item.GetValueOrDefault("sourceSk")?.S ?? "";
        var data = item.GetValueOrDefault("recordData")?.M;
        if (sourcePk.StartsWith("REGISTRATION#", StringComparison.Ordinal) && sourceSk.StartsWith("EVENT#", StringComparison.Ordinal) && data is { Count: > 0 })
        {
            var value = Document.FromAttributeMap(data).ToJson();
            var row = await db.ResidentEvents.FindAsync([source, key], ct);
            if (row is null) db.ResidentEvents.Add(new ResidentEvent { Source = source, Key = key, RegistrationId = sourcePk[13..], EventKey = sourceSk, Data = value });
            else row.Data = value;
        }
        if (sourcePk.StartsWith("USER#", StringComparison.Ordinal) && sourceSk == "MEMBERSHIP" && data is { Count: > 0 } && long.TryParse(sourcePk[5..], out var id))
        {
            var membershipComplex = item.GetValueOrDefault("complexId")?.S;
            if (string.IsNullOrEmpty(membershipComplex)) membershipComplex = data.GetValueOrDefault("buildingId")?.S ?? "";
            var row = await db.ResidentMemberships.FindAsync([source, key], ct);
            if (row is null)
            {
                row = new ResidentMembership { Source = source, Key = key, ComplexId = membershipComplex, TelegramUserId = id };
                db.ResidentMemberships.Add(row);
            }
            row.ComplexId = membershipComplex;
            row.Active = data.GetValueOrDefault("active")?.BOOL == true;
            row.RegistrationId = data.GetValueOrDefault("registrationId")?.S;
            row.ReviewedBy = data.GetValueOrDefault("reviewedBy")?.S;
            row.ReviewedAt = Date(data.GetValueOrDefault("reviewedAt")?.S);
        }
        var pk = item.GetValueOrDefault("pk")?.S ?? "";
        if (!pk.StartsWith("COMPLEX#", StringComparison.Ordinal) || !pk.Contains("#USER#", StringComparison.Ordinal) || pk.Contains("#RECORD#", StringComparison.Ordinal) || !item.TryGetValue("payload", out var payload) || string.IsNullOrEmpty(payload.S)) return;
        using var profile = JsonDocument.Parse(payload.S);
        var root = profile.RootElement;
        if (!root.TryGetProperty("UserId", out var user) || !root.TryGetProperty("BuildingId", out var complex)) return;
        var complexId = complex.GetString()!;
        var userId = user.GetInt64();
        await db.ResidentPhones.Where(x => x.Source == source && x.ComplexId == complexId && x.TelegramUserId == userId).ExecuteDeleteAsync(ct);
        var stage = String(root, "Stage");
        var deleted = root.TryGetProperty("PersonalDataDeleted", out var deletedValue) && deletedValue.ValueKind == JsonValueKind.True ||
            stage == "Deleted" || String(root, "PersonalDataDeletedAt") is not null && stage != "Approved";
        var phone = deleted ? null : String(root, "Phone");
        if (!string.IsNullOrEmpty(phone)) db.ResidentPhones.Add(new ResidentPhone { Source = source, ComplexId = complexId, TelegramUserId = userId, Phone = phone });
        var consent = await db.ResidentConsents.FindAsync([source, complexId, userId], ct);
        var consentAt = deleted ? null : Date(String(root, "ConsentAt"));
        if (consentAt is null) { if (consent is not null) db.ResidentConsents.Remove(consent); }
        else if (consent is null) db.ResidentConsents.Add(new ResidentConsent { Source = source, ComplexId = complexId, TelegramUserId = userId, ConsentedAt = consentAt.Value, Version = String(root, "ConsentVersion") });
        else { consent.ConsentedAt = consentAt.Value; consent.Version = String(root, "ConsentVersion"); }
    }

    public async Task RemoveAsync(string source, string table, string key, string json, CancellationToken ct)
    {
        var parts = key.Split('\u001f', 2);
        var pk = parts[0];
        var sk = parts.Length > 1 ? parts[1] : "";
        if (table == "administrators")
        {
            await db.AdministratorPermissions.Where(x => x.Source == source && x.UserId == key).ExecuteDeleteAsync(ct);
            await db.AdministratorNotificationSettings.Where(x => x.Source == source && x.UserId == key).ExecuteDeleteAsync(ct);
        }
        if (table == "command-data")
        {
            if (pk.StartsWith("USER#", StringComparison.Ordinal) && sk.StartsWith("REQUEST#", StringComparison.Ordinal) &&
                long.TryParse(pk[5..], out var owner) && long.TryParse(sk[8..], out var request))
                await db.RequestKeyItems.Where(x => x.Source == source && x.OwnerId == owner && x.RequestId == request).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("REQUEST#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
                await db.RequestEvents.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("MESSAGES#", StringComparison.Ordinal) && sk.StartsWith("MESSAGE#", StringComparison.Ordinal))
                await db.BroadcastMessages.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("NOTIFICATIONS#", StringComparison.Ordinal))
                await db.Notifications.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
            if (pk.StartsWith("ADMIN#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
                await db.AdministratorEvents.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
        }
        if (table == "statistics" && pk == "API")
            await db.ApiDailyStatistics.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
        if (table != "residents") return;
        using var record = JsonDocument.Parse(json);
        var sourcePk = String(record.RootElement, "sourcePk") ?? "";
        var sourceSk = String(record.RootElement, "sourceSk") ?? "";
        if (sourcePk.StartsWith("REGISTRATION#", StringComparison.Ordinal) && sourceSk.StartsWith("EVENT#", StringComparison.Ordinal))
            await db.ResidentEvents.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
        if (sourcePk.StartsWith("USER#", StringComparison.Ordinal) && sourceSk == "MEMBERSHIP")
            await db.ResidentMemberships.Where(x => x.Source == source && x.Key == key).ExecuteDeleteAsync(ct);
        if (!pk.StartsWith("COMPLEX#", StringComparison.Ordinal) || !pk.Contains("#USER#", StringComparison.Ordinal) || pk.Contains("#RECORD#", StringComparison.Ordinal)) return;
        var payload = String(record.RootElement, "payload");
        if (payload is null) return;
        using var profile = JsonDocument.Parse(payload);
        if (!profile.RootElement.TryGetProperty("UserId", out var user) || !profile.RootElement.TryGetProperty("BuildingId", out var complex)) return;
        var complexId = complex.GetString();
        var userId = user.GetInt64();
        await db.ResidentPhones.Where(x => x.Source == source && x.ComplexId == complexId && x.TelegramUserId == userId).ExecuteDeleteAsync(ct);
        await db.ResidentConsents.Where(x => x.Source == source && x.ComplexId == complexId && x.TelegramUserId == userId).ExecuteDeleteAsync(ct);
    }
    private static string? String(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
    private static double Number(Dictionary<string, AttributeValue> item, string key) => double.TryParse(item.GetValueOrDefault(key)?.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
