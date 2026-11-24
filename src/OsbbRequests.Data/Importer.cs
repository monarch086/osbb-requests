using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.EntityFrameworkCore;
using OsbbRequests.Data.Storage;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Data;

public sealed class Importer(DataContext db, IAmazonDynamoDB dynamo, string stage, string region)
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter<RequestStatus>(), new JsonStringEnumConverter<RequestVisibility>() }
    };
    private readonly string[] tables = ["administrators", "command-data", "residents", "statistics"];
    public async Task ImportAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(349875242)", ct);
        var run = new ImportRun { Id = Guid.NewGuid(), Source = $"unknown:{region}:{stage}", StartedAt = DateTimeOffset.UtcNow, Status = "running" };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        try
        {
            var sources = new List<(string Kind, string Name, string Account)>();
            foreach (var kind in tables)
            {
                var name = $"osbb-requests-{stage}-{kind}";
                var description = await dynamo.DescribeTableAsync(new DescribeTableRequest { TableName = name }, ct);
                var parts = description.Table.TableArn.Split(':');
                if (parts.Length < 6 || parts[3] != region) throw new InvalidOperationException("Unexpected source table ARN.");
                sources.Add((kind, name, parts[4]));
            }
            if (sources.Select(s => s.Account).Distinct(StringComparer.Ordinal).Count() != 1) throw new InvalidOperationException("Source account mismatch.");
            var source = $"{sources[0].Account}:{region}:{stage}";
            run.Source = source;
            var apartments = new OwnerApartmentProjector(db);
            foreach (var table in sources)
            {
                Dictionary<string, AttributeValue>? start = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    var page = await dynamo.ScanAsync(new ScanRequest { TableName = table.Name, ConsistentRead = true, Limit = 100, ExclusiveStartKey = start }, ct);
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    foreach (var item in page.Items ?? [])
                    {
                        ct.ThrowIfCancellationRequested();
                        var key = table.Kind == "administrators" ? item["UserId"].S : item["pk"].S + "\u001f" + (item.GetValueOrDefault("sk")?.S ?? "");
                        seen.Add(key);
                        run.ReadCount++;
                        if (table.Kind == "residents" && OwnerApartmentProjector.TryParseKey(item["pk"].S, out var complexId, out var ownerId))
                        {
                            var changed = await apartments.ProjectAsync(source, complexId, ownerId, item, ct);
                            var legacyCopy = await db.SourceRecords.FindAsync([source, table.Kind, key], ct);
                            if (legacyCopy is not null) db.SourceRecords.Remove(legacyCopy);
                            if (changed || legacyCopy is not null) run.ChangedCount++;
                            continue;
                        }
                        var json = Document.FromAttributeMap(item).ToJson();
                        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(json))));
                        var existing = await db.SourceRecords.FindAsync([source, table.Kind, key], ct);
                        if (existing?.Hash == hash && existing.ProjectionVersion == RelatedProjector.Version) continue;
                        if (existing is null) db.SourceRecords.Add(new SourceRecord { Source = source, Table = table.Kind, Key = key, Hash = hash, Data = json, LastSeenAt = run.StartedAt, ProjectionVersion = RelatedProjector.Version });
                        else { existing.Hash = hash; existing.Data = json; existing.LastSeenAt = run.StartedAt; existing.ProjectionVersion = RelatedProjector.Version; }
                        await ProjectAsync(source, table.Kind, item, json, ct);
                        await new RelatedProjector(db).ProjectAsync(source, table.Kind, key, item, json, ct);
                        run.ChangedCount++;
                    }
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    db.ChangeTracker.Clear();
                    db.Attach(run);
                    start = page.LastEvaluatedKey;
                } while (start is { Count: > 0 });
                var savedKeys = await db.SourceRecords.AsNoTracking().Where(row => row.Source == source && row.Table == table.Kind)
                    .Select(row => row.Key).ToHashSetAsync(ct);
                if (table.Kind == "residents")
                {
                    var owners = await db.ResidentApartments.Where(row => row.Source == source)
                        .Select(row => new { row.ComplexId, row.TelegramUserId }).Distinct().ToListAsync(ct);
                    savedKeys.UnionWith(owners.Select(owner =>
                        OwnerApartments.Key(owner.ComplexId, owner.TelegramUserId) + "\u001f"));
                }
                foreach (var key in savedKeys.Where(key => !seen.Contains(key)))
                {
                    var parts = key.Split('\u001f', 2);
                    var dynamoKey = table.Kind == "administrators" ? new Dictionary<string, AttributeValue> { ["UserId"] = new(key) }
                        : new Dictionary<string, AttributeValue> { ["pk"] = new(parts[0]) };
                    if (table.Kind is "command-data" or "statistics") dynamoKey["sk"] = new(parts[1]);
                    var current = await dynamo.GetItemAsync(new GetItemRequest { TableName = table.Name, Key = dynamoKey, ConsistentRead = true }, ct);
                    if (current.Item is { Count: > 0 }) continue;
                    var stale = await db.SourceRecords.FindAsync([source, table.Kind, key], ct);
                    var complexId = "";
                    long ownerId = 0;
                    var isApartment = table.Kind == "residents" && OwnerApartmentProjector.TryParseKey(parts[0], out complexId, out ownerId);
                    if (stale is null && !isApartment) continue;
                    await using var deletion = await db.Database.BeginTransactionAsync(ct);
                    if (isApartment)
                        await apartments.RemoveAsync(source, complexId, ownerId, ct);
                    if (stale is not null)
                    {
                        if (!isApartment)
                        {
                            await RemoveProjectionAsync(source, table.Kind, key, stale.Data, ct);
                            await new RelatedProjector(db).RemoveAsync(source, table.Kind, key, stale.Data, ct);
                        }
                        db.SourceRecords.Remove(stale);
                    }
                    await db.SaveChangesAsync(ct);
                    await deletion.CommitAsync(ct);
                    db.ChangeTracker.Clear();
                    db.Attach(run);
                }
                Console.WriteLine($"{table.Kind}: завершено.");
            }
            run.Status = "succeeded";
        }
        catch
        {
            run.Status = "failed";
            throw;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            db.ChangeTracker.Clear();
            db.Attach(run);
            db.Entry(run).State = EntityState.Modified;
            await db.SaveChangesAsync(CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
        Console.WriteLine($"Імпорт завершено. Прочитано: {run.ReadCount}; змінено: {run.ChangedCount}.");
    }
    private async Task RemoveProjectionAsync(string source, string table, string key, string data, CancellationToken ct)
    {
        if (table == "administrators")
        {
            var admin = await db.Administrators.FindAsync([source, key], ct);
            if (admin is not null) db.Administrators.Remove(admin);
        }
        var parts = key.Split('\u001f', 2);
        if (table == "command-data" && parts[0].StartsWith("USER#", StringComparison.Ordinal) && parts[1].StartsWith("REQUEST#", StringComparison.Ordinal))
        {
            if (long.TryParse(parts[0][5..], out var owner) && long.TryParse(parts[1][8..], out var id))
            {
                var request = await db.Requests.FindAsync([source, owner, id], ct);
                if (request is not null) db.Requests.Remove(request);
            }
        }
        if (table == "residents" && parts[0].StartsWith("COMPLEX#", StringComparison.Ordinal) && parts[0].Contains("#USER#", StringComparison.Ordinal) && !parts[0].Contains("#RECORD#", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.String) return;
            using var profile = JsonDocument.Parse(payload.GetString()!);
            if (!profile.RootElement.TryGetProperty("UserId", out var user) || !profile.RootElement.TryGetProperty("BuildingId", out var complex)) return;
            var resident = await db.Residents.FindAsync([source, complex.GetString()!, user.GetInt64()], ct);
            if (resident is not null) db.Residents.Remove(resident);
        }
    }
    private async Task ProjectAsync(string source, string table, Dictionary<string, AttributeValue> item, string json, CancellationToken ct)
    {
        if (table == "administrators")
        {
            var id = item["UserId"].S;
            var admin = await db.Administrators.FindAsync([source, id], ct);
            if (admin is null) db.Administrators.Add(new Administrator { Source = source, UserId = id, Data = json });
            else admin.Data = json;
        }
        if (!item.TryGetValue("payload", out var payload) || string.IsNullOrEmpty(payload.S)) return;
        var pk = item.GetValueOrDefault("pk")?.S ?? "";
        var sk = item.GetValueOrDefault("sk")?.S ?? "";
        using var data = JsonDocument.Parse(payload.S);
        var root = data.RootElement;
        if (table == "residents" && pk.StartsWith("COMPLEX#", StringComparison.Ordinal) && pk.Contains("#USER#", StringComparison.Ordinal) && !pk.Contains("#RECORD#", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("UserId", out var user) || !root.TryGetProperty("BuildingId", out var complex)) return;
            var complexId = complex.GetString()!;
            var id = user.GetInt64();
            var profile = root.Deserialize<ResidentRegistration>()!;
            var next = new Resident
            {
                Source = source,
                ComplexId = complexId,
                TelegramUserId = id,
                RegistrationId = profile.Id,
                Stage = profile.Stage,
                Version = profile.Version,
                CreatedAt = root.TryGetProperty("CreatedAt", out _) ? profile.CreatedAt.ToUniversalTime() : null,
                UpdatedAt = root.TryGetProperty("UpdatedAt", out _) ? profile.UpdatedAt.ToUniversalTime() : null,
                Phone = profile.Phone,
                FirstName = profile.FirstName,
                LastName = profile.LastName,
                DecisionReason = profile.DecisionReason,
                BuildingNumber = profile.BuildingNumber,
                ApartmentNumber = profile.ApartmentNumber,
                PersonalDataDeleted = profile.PersonalDataDeleted,
                PersonalDataDeletedAt = profile.PersonalDataDeletedAt?.ToUniversalTime(),
                ApprovedAt = profile.ApprovedAt?.ToUniversalTime(),
                Email = profile.Email,
                ConsentAt = profile.ConsentAt?.ToUniversalTime(),
                ConsentVersion = profile.ConsentVersion,
                ResumeAfterConsent = profile.ResumeAfterConsent,
                ResidentType = profile.ResidentType,
                TypeBeforeAddress = profile.TypeBeforeAddress,
                TypeChangeCount = profile.TypeChangeCount
            };
            var resident = await db.Residents.FindAsync([source, complexId, id], ct);
            if (resident is null) db.Residents.Add(next);
            else db.Entry(resident).CurrentValues.SetValues(next);
        }
        if (table == "command-data" && pk.StartsWith("USER#", StringComparison.Ordinal) && sk.StartsWith("REQUEST#", StringComparison.Ordinal))
        {
            var value = root.Deserialize<MaintenanceRequest>(RequestJsonOptions)!;
            var next = new Request
            {
                Source = source,
                OwnerId = value.OwnerId,
                RequestId = value.Id,
                ComplexId = value.BuildingId,
                CreatedAt = value.CreatedAt.ToUniversalTime(),
                Status = value.Status,
                Category = value.Category,
                Location = value.Location,
                Description = value.Description,
                Visibility = value.Visibility,
                Resolution = value.Resolution,
                AssignedTo = value.AssignedTo,
                Priority = value.Priority ?? "Normal",
                PlannedDate = value.PlannedDate,
                Scope = value.Scope,
                BuildingNumber = value.BuildingNumber,
                Floor = value.Floor,
                Entrance = value.Entrance,
                KeyType = value.KeyType,
                KeyQuantity = value.KeyQuantity
            };
            var request = await db.Requests.FindAsync([source, value.OwnerId, value.Id], ct);
            if (request is null) db.Requests.Add(next);
            else db.Entry(request).CurrentValues.SetValues(next);
        }
    }
    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }
}
