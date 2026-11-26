using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.DataImport.Storage;

namespace OsbbRequests.DataImport;

internal sealed class RelatedProjector(DataContext db, ComplexDirectory complexes)
{
    public const int Version = 2;

    public Task ProjectAsync(long sourceId, string table, string key, Dictionary<string, AttributeValue> item, string json, CancellationToken ct) =>
        table switch
        {
            "administrators" => ProjectAdministratorAsync(sourceId, item, ct),
            "command-data" => ProjectCommandAsync(sourceId, key, item, json, ct),
            "residents" => ProjectResidentAsync(sourceId, key, item, ct),
            "statistics" => ProjectStatisticsAsync(sourceId, item, ct),
            _ => Task.CompletedTask
        };

    private Task ProjectCommandAsync(long sourceId, string key, Dictionary<string, AttributeValue> item, string json, CancellationToken ct)
    {
        var pk = AttributeText(item, "pk") ?? "";
        var sk = AttributeText(item, "sk") ?? "";
        if (pk.StartsWith("USER#", StringComparison.Ordinal) && sk.StartsWith("REQUEST#", StringComparison.Ordinal))
            return ProjectRequestKeysAsync(sourceId, pk, sk, item, ct);
        if (pk.StartsWith("REQUEST#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal) && long.TryParse(pk[8..], out var request))
            return ProjectRequestEventAsync(sourceId, key, request, sk, item, json, ct);
        return ProjectOperationalRecordAsync(sourceId, key, pk, sk, item, json, ct);
    }

    private Task ProjectOperationalRecordAsync(long sourceId, string key, string pk, string sk,
        Dictionary<string, AttributeValue> item, string json, CancellationToken ct)
    {
        if (pk.StartsWith("MESSAGES#", StringComparison.Ordinal) && sk.StartsWith("MESSAGE#", StringComparison.Ordinal))
            return ProjectBroadcastAsync(sourceId, key, pk[9..], item, ct);
        if (pk.StartsWith("NOTIFICATIONS#", StringComparison.Ordinal))
            return ProjectNotificationAsync(sourceId, key, pk[14..], sk, json, ct);
        if (pk.StartsWith("ADMIN#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
            return ProjectAdministratorEventAsync(sourceId, key, pk[6..], json, ct);
        return Task.CompletedTask;
    }

    private async Task ProjectAdministratorAsync(long sourceId, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var id = item["UserId"].S;
        var admin = await db.Administrators.FindAsync([sourceId, id], ct);
        if (admin is null)
        {
            admin = new Administrator { SourceId = sourceId, UserId = id };
            db.Administrators.Add(admin);
        }
        admin.Email = AttributeText(item, "Email");
        admin.DisplayName = AttributeText(item, "DisplayName");
        admin.IsActive = item.GetValueOrDefault("IsActive")?.BOOL == true;
        admin.IsSuperAdmin = Flag(item, "IsSuperAdmin", false);
        admin.Version = int.Parse(item.GetValueOrDefault("Version")?.N ?? "0", CultureInfo.InvariantCulture);
        admin.CreatedAt = Date(AttributeText(item, "CreatedAt"));
        admin.UpdatedAt = Date(AttributeText(item, "UpdatedAt"));

        await ProjectAdministratorPermissionsAsync(sourceId, id, item, ct);
        await ProjectAdministratorNotificationsAsync(sourceId, id, item, ct);
    }

    private async Task ProjectAdministratorPermissionsAsync(long sourceId, string id,
        Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var permissions = await db.AdministratorPermissions.FindAsync([sourceId, id], ct);
        if (permissions is null)
        {
            permissions = new AdministratorPermission { SourceId = sourceId, UserId = id };
            db.AdministratorPermissions.Add(permissions);
        }
        permissions.ComplexRequests = Flag(item, "AccessComplexRequests", true);
        permissions.KeyOrders = Flag(item, "AccessKeyOrders", true);
        permissions.RegisteredResidents = Flag(item, "AccessRegisteredResidents", true);
        permissions.PendingResidents = Flag(item, "AccessPendingResidents", true);
        permissions.Messages = Flag(item, "AccessMessages", false);
    }

    private async Task ProjectAdministratorNotificationsAsync(long sourceId, string id,
        Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var settings = await db.AdministratorNotificationSettings.FindAsync([sourceId, id], ct);
        if (settings is null)
        {
            settings = new AdministratorNotificationSetting { SourceId = sourceId, UserId = id };
            db.AdministratorNotificationSettings.Add(settings);
        }
        settings.TelegramChatId = AttributeText(item, "TelegramChatId");
        settings.NewResidents = Flag(item, "NotifyNewResidents", false);
        settings.NewApartmentRegistrations = Flag(item, "NotifyNewApartmentRegistrations", settings.NewResidents);
        settings.NewRequests = Flag(item, "NotifyNewRequests", false);
        settings.NewKeyOrders = Flag(item, "NotifyNewKeyOrders", settings.NewRequests);
        settings.NewAdministrators = Flag(item, "NotifyNewAdministrators", false);
    }

    private async Task ProjectRequestKeysAsync(long sourceId, string pk, string sk, Dictionary<string, AttributeValue> item, CancellationToken ct)
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

    private async Task ProjectRequestEventAsync(long sourceId, string key, long request, string sk, Dictionary<string, AttributeValue> item, string json, CancellationToken ct)
    {
        var row = await db.RequestEvents.FindAsync([sourceId, key], ct);
        if (row is null) db.RequestEvents.Add(new RequestEvent { SourceId = sourceId, Key = key, RequestId = request, EventKey = sk, CreatedAt = Date(item.GetValueOrDefault("createdAt")?.S), Data = json });
        else { row.Data = json; row.CreatedAt = Date(item.GetValueOrDefault("createdAt")?.S); }
    }

    private async Task ProjectBroadcastAsync(long sourceId, string key, string complex, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(item["payload"].S);
        var data = payload.RootElement;
        var row = await db.BroadcastMessages.FindAsync([sourceId, key], ct);
        if (row is null)
        {
            row = new BroadcastMessage { SourceId = sourceId, Key = key, ComplexId = await complexes.GetOrCreateIdAsync(complex, ct), MessageId = String(data, "Id") ?? "", Text = "", CreatedById = "", CreatedBy = "" };
            db.BroadcastMessages.Add(row);
        }
        row.Text = String(data, "Text") ?? "";
        row.CreatedBy = String(data, "CreatedBy") ?? "";
        row.CreatedById = String(data, "CreatedById") ?? "";
        row.CreatedAt = Date(String(data, "CreatedAt")) ?? throw new InvalidOperationException("Invalid message date.");
    }

    private async Task ProjectNotificationAsync(long sourceId, string key, string complex, string sk, string json, CancellationToken ct)
    {
        var row = await db.Notifications.FindAsync([sourceId, key], ct);
        if (row is null) db.Notifications.Add(new Notification { SourceId = sourceId, Key = key, ComplexId = await complexes.GetOrCreateIdAsync(complex, ct), Kind = sk.Split('#', 2)[0], Data = json });
        else row.Data = json;
    }

    private async Task ProjectAdministratorEventAsync(long sourceId, string key, string administratorId, string json, CancellationToken ct)
    {
        var row = await db.AdministratorEvents.FindAsync([sourceId, key], ct);
        if (row is null) db.AdministratorEvents.Add(new AdministratorEvent { SourceId = sourceId, Key = key, AdministratorId = administratorId, Data = json });
        else row.Data = json;
    }

    private async Task ProjectStatisticsAsync(long sourceId, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        if (item.GetValueOrDefault("pk")?.S != "API" ||
            !TryParseStatisticKey(AttributeText(item, "sk") ?? "", out var date, out var endpoint)) return;
        var row = await db.ApiDailyStatistics.FindAsync([sourceId, date, endpoint], ct);
        if (row is null) { row = new ApiDailyStatistic { SourceId = sourceId, Date = date, Endpoint = endpoint }; db.ApiDailyStatistics.Add(row); }
        row.Requests = (long)Number(item, "requests"); row.ReadUnits = Number(item, "readUnits");
        row.WriteUnits = Number(item, "writeUnits"); row.DurationMs = Number(item, "durationMs");
    }

    private async Task ProjectResidentAsync(long sourceId, string key, Dictionary<string, AttributeValue> item, CancellationToken ct)
    {
        var sourcePk = AttributeText(item, "sourcePk") ?? "";
        var sourceSk = AttributeText(item, "sourceSk") ?? "";
        var data = item.GetValueOrDefault("recordData")?.M;
        if (data is not { Count: > 0 }) return;
        if (IsRegistrationEvent(sourcePk, sourceSk))
            await ProjectResidentEventAsync(sourceId, key, sourcePk[13..], sourceSk, data, ct);
        if (IsMembership(sourcePk, sourceSk) && long.TryParse(sourcePk[5..], out var id))
            await ProjectMembershipAsync(sourceId, key, id, AttributeText(item, "complexId"), data, ct);
    }

    private async Task ProjectResidentEventAsync(long sourceId, string key, string reference, string sourceSk,
        Dictionary<string, AttributeValue> data, CancellationToken ct)
    {
        var row = await db.ResidentEvents.FindAsync([sourceId, key], ct);
        var next = new ResidentEvent
        {
            SourceId = sourceId,
            Key = key,
            RegistrationId = reference,
            EventKey = sourceSk,
            Data = Document.FromAttributeMap(data).ToJson(),
            Actor = data.GetValueOrDefault("actor")?.S,
            CreatedAt = Date(data.GetValueOrDefault("createdAt")?.S)
        };
        if (sourceSk.StartsWith("EVENT#TYPE#", StringComparison.Ordinal) && data.GetValueOrDefault("payload")?.S is { Length: > 0 } payload)
            ProjectResidentHistory(next, payload);
        if (row is null) db.ResidentEvents.Add(next);
        else db.Entry(row).CurrentValues.SetValues(next);
    }

    private async Task ProjectMembershipAsync(long sourceId, string key, long id, string? membershipComplex,
        Dictionary<string, AttributeValue> data, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(membershipComplex)) membershipComplex = AttributeText(data, "buildingId");
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
        row.RegistrationId = AttributeText(data, "registrationId");
        row.ReviewedBy = AttributeText(data, "reviewedBy");
        row.ReviewedAt = Date(AttributeText(data, "reviewedAt"));
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

    private static void ProjectResidentHistory(ResidentEvent row, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        row.Actor = String(root, "Actor") ?? row.Actor;
        row.CreatedAt = Date(String(root, "CreatedAt")) ?? row.CreatedAt;
        if (!root.TryGetProperty("KeyChange", out var keyChange) || keyChange.ValueKind != JsonValueKind.Object) return;
        row.KeyRequestId = keyChange.GetProperty("RequestId").GetInt64();
        var previous = keyChange.GetProperty("Previous");
        row.PreviousKeyType = String(previous, "Type");
        row.PreviousKeySerialNumber = String(previous, "SerialNumber");
        if (!keyChange.TryGetProperty("Current", out var current) || current.ValueKind != JsonValueKind.Object) return;
        row.CurrentKeyType = String(current, "Type");
        row.CurrentKeySerialNumber = String(current, "SerialNumber");
    }

    public Task RemoveAsync(long sourceId, string table, string key, string json, CancellationToken ct) => table switch
    {
        "administrators" => RemoveAdministratorSettingsAsync(sourceId, key, ct),
        "command-data" => RemoveCommandAsync(sourceId, key, ct),
        "statistics" => RemoveStatisticsAsync(sourceId, key, ct),
        "residents" => RemoveResidentRecordAsync(sourceId, key, json, ct),
        _ => Task.CompletedTask
    };

    private async Task RemoveAdministratorSettingsAsync(long sourceId, string key, CancellationToken ct)
    {
        await db.AdministratorPermissions.Where(x => x.SourceId == sourceId && x.UserId == key).ExecuteDeleteAsync(ct);
        await db.AdministratorNotificationSettings.Where(x => x.SourceId == sourceId && x.UserId == key).ExecuteDeleteAsync(ct);
    }

    private Task RemoveCommandAsync(long sourceId, string key, CancellationToken ct)
    {
        var (pk, sk) = SplitKey(key);
        if (TryRequestKey(pk, sk, out var owner, out var request))
            return db.RequestKeyItems.Where(x => x.SourceId == sourceId && x.OwnerId == owner && x.RequestId == request).ExecuteDeleteAsync(ct);
        if (pk.StartsWith("REQUEST#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
            return db.RequestEvents.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        return RemoveOperationalRecordAsync(sourceId, key, pk, sk, ct);
    }

    private Task RemoveOperationalRecordAsync(long sourceId, string key, string pk, string sk, CancellationToken ct)
    {
        if (pk.StartsWith("MESSAGES#", StringComparison.Ordinal) && sk.StartsWith("MESSAGE#", StringComparison.Ordinal))
            return db.BroadcastMessages.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        if (pk.StartsWith("NOTIFICATIONS#", StringComparison.Ordinal))
            return db.Notifications.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        if (pk.StartsWith("ADMIN#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal))
            return db.AdministratorEvents.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        return Task.CompletedTask;
    }

    private async Task RemoveStatisticsAsync(long sourceId, string key, CancellationToken ct)
    {
        var (pk, sk) = SplitKey(key);
        if (pk == "API" && TryParseStatisticKey(sk, out var date, out var endpoint))
            await db.ApiDailyStatistics.Where(x => x.SourceId == sourceId && x.Date == date && x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
    }

    private async Task RemoveResidentRecordAsync(long sourceId, string key, string json, CancellationToken ct)
    {
        using var record = JsonDocument.Parse(json);
        var sourcePk = String(record.RootElement, "sourcePk") ?? "";
        var sourceSk = String(record.RootElement, "sourceSk") ?? "";
        if (IsRegistrationEvent(sourcePk, sourceSk))
            await db.ResidentEvents.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
        if (IsMembership(sourcePk, sourceSk))
            await db.ResidentMemberships.Where(x => x.SourceId == sourceId && x.Key == key).ExecuteDeleteAsync(ct);
    }
    private static bool IsRegistrationEvent(string pk, string sk) =>
        pk.StartsWith("REGISTRATION#", StringComparison.Ordinal) && sk.StartsWith("EVENT#", StringComparison.Ordinal);

    private static bool IsMembership(string pk, string sk) =>
        pk.StartsWith("USER#", StringComparison.Ordinal) && sk == "MEMBERSHIP";

    private static (string Partition, string Sort) SplitKey(string key)
    {
        var parts = key.Split('\u001f', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }

    private static bool TryRequestKey(string pk, string sk, out long owner, out long request)
    {
        owner = request = default;
        return pk.StartsWith("USER#", StringComparison.Ordinal) && sk.StartsWith("REQUEST#", StringComparison.Ordinal) &&
            long.TryParse(pk[5..], out owner) && long.TryParse(sk[8..], out request);
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

    private static string? AttributeText(Dictionary<string, AttributeValue> item, string key) => item.GetValueOrDefault(key)?.S;
    private static bool Flag(Dictionary<string, AttributeValue> item, string key, bool fallback) => item.GetValueOrDefault(key)?.BOOL ?? fallback;
    private static string? String(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
    private static double Number(Dictionary<string, AttributeValue> item, string key) => double.TryParse(item.GetValueOrDefault(key)?.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
