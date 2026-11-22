using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OsbbRequests.Data;
using OsbbRequests.Data.Storage;
using OsbbRequests.Shared.Residents;

var builder = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Database = "osbb_import_test", Username = "osbb", Password = Environment.GetEnvironmentVariable("OSBB_POSTGRES_PASSWORD") };
await using var db = new DataContext(new DbContextOptionsBuilder<DataContext>().UseNpgsql(builder.ConnectionString).Options);
await db.Database.MigrateAsync();
var stage = "smoke-" + Guid.NewGuid().ToString("N");
using var dynamo = new FakeDynamo();
async Task Import() { await new Importer(db, dynamo, stage, "eu-central-1").ImportAsync(default); db.ChangeTracker.Clear(); }
await Import();
var source = "123456789012:eu-central-1:" + stage;
if (await db.Residents.CountAsync(x => x.Source == source) != 1 || await db.Requests.CountAsync(x => x.Source == source) != 1 ||
    await db.Administrators.CountAsync(x => x.Source == source) != 1 || await db.RequestKeyItems.CountAsync(x => x.Source == source) != 1 ||
    await db.RequestEvents.CountAsync(x => x.Source == source) != 1 || await db.ResidentEvents.CountAsync(x => x.Source == source) != 1 ||
    await db.ResidentPhones.CountAsync(x => x.Source == source) != 1 || await db.ResidentConsents.CountAsync(x => x.Source == source) != 1 ||
    await db.ResidentMemberships.CountAsync(x => x.Source == source) != 1 || await db.BroadcastMessages.CountAsync(x => x.Source == source) != 1 ||
    await db.Notifications.CountAsync(x => x.Source == source) != 1 || await db.AdministratorEvents.CountAsync(x => x.Source == source) != 1 ||
    await db.AdministratorPermissions.CountAsync(x => x.Source == source) != 1 || await db.AdministratorNotificationSettings.CountAsync(x => x.Source == source) != 1 ||
    await db.ApiDailyStatistics.CountAsync(x => x.Source == source) != 1) throw new Exception("Projection failed");
var apartment = await db.ResidentApartments.SingleAsync(x => x.Source == source);
if (apartment.RegistrationId != "ref" || apartment.BuildingNumber != "4Г" || apartment.ApartmentNumber != 12 ||
    apartment.Status != ApartmentReviewStatus.Pending || apartment.ReviewedAt is not null || apartment.ReviewedBy is not null ||
    await db.SourceRecords.AnyAsync(x => x.Source == source && x.Key == FakeDynamo.ApartmentKey + "\u001f"))
    throw new Exception("Relational apartment import failed");
var permissions = await db.AdministratorPermissions.SingleAsync(x => x.Source == source);
var settings = await db.AdministratorNotificationSettings.SingleAsync(x => x.Source == source);
if (!permissions.ComplexRequests || !permissions.KeyOrders || permissions.Messages || !settings.NewRequests || !settings.NewKeyOrders)
    throw new Exception("Legacy administrator defaults failed");
await Import();
var run = await db.ImportRuns.Where(x => x.Source == source).OrderByDescending(x => x.StartedAt).FirstAsync();
if (run.ChangedCount != 0 || run.Status != "succeeded") throw new Exception("Repeat failed");
db.SourceRecords.Add(new SourceRecord { Source = source, Table = "residents", Key = FakeDynamo.ApartmentKey + "\u001f",
    Data = "{\"payload\":\"legacy\"}", Hash = "legacy", LastSeenAt = DateTimeOffset.UtcNow });
await db.SaveChangesAsync();
await Import();
if (await db.SourceRecords.AnyAsync(x => x.Source == source && x.Key == FakeDynamo.ApartmentKey + "\u001f"))
    throw new Exception("Legacy JSON apartment copy survived import");
dynamo.ApartmentApproved = true;
await Import();
apartment = await db.ResidentApartments.SingleAsync(x => x.Source == source);
if (apartment.Status != ApartmentReviewStatus.Approved || apartment.ReviewedBy != "reviewer@example.test" ||
    apartment.ReviewedAt != DateTimeOffset.Parse("2026-01-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
    throw new Exception("Apartment decision was not updated");
dynamo.SkipApartmentInScan = true;
await Import();
if (!await db.ResidentApartments.AnyAsync(x => x.Source == source)) throw new Exception("Unseen but existing apartment record was deleted");
dynamo.ApartmentRemoved = true;
await Import();
if (await db.ResidentApartments.AnyAsync(x => x.Source == source)) throw new Exception("Deleted apartment record remained");
dynamo.SkipApartmentInScan = false;
dynamo.ApartmentRemoved = false;
await db.SourceRecords.Where(x => x.Source == source).ExecuteUpdateAsync(x => x.SetProperty(row => row.ProjectionVersion, 0));
await db.RequestEvents.Where(x => x.Source == source).ExecuteDeleteAsync();
await Import();
if (await db.RequestEvents.CountAsync(x => x.Source == source) != 1) throw new Exception("Projection upgrade failed");
dynamo.Serial = "new-serial";
await Import();
var key = await db.RequestKeyItems.SingleAsync(x => x.Source == source);
if (key.SerialNumber != "new-serial") throw new Exception("Key update failed");
dynamo.Deleted = true;
await Import();
if (await db.ResidentPhones.AnyAsync(x => x.Source == source) || await db.ResidentConsents.AnyAsync(x => x.Source == source)) throw new Exception("Deleted profile retained personal data projections");
dynamo.Deleted = false;
await Import();
if (await db.ResidentPhones.CountAsync(x => x.Source == source) != 1 || await db.ResidentConsents.CountAsync(x => x.Source == source) != 1) throw new Exception("Restored profile missing projections");
dynamo.Empty = true;
await Import();
if (await db.SourceRecords.AnyAsync(x => x.Source == source) || await db.RequestKeyItems.AnyAsync(x => x.Source == source) ||
    await db.ResidentApartments.AnyAsync(x => x.Source == source) ||
    await db.RequestEvents.AnyAsync(x => x.Source == source) || await db.ResidentEvents.AnyAsync(x => x.Source == source) ||
    await db.ResidentPhones.AnyAsync(x => x.Source == source) || await db.ResidentConsents.AnyAsync(x => x.Source == source) ||
    await db.ResidentMemberships.AnyAsync(x => x.Source == source) || await db.BroadcastMessages.AnyAsync(x => x.Source == source) ||
    await db.Notifications.AnyAsync(x => x.Source == source) || await db.AdministratorEvents.AnyAsync(x => x.Source == source) ||
    await db.AdministratorPermissions.AnyAsync(x => x.Source == source) || await db.AdministratorNotificationSettings.AnyAsync(x => x.Source == source) ||
    await db.ApiDailyStatistics.AnyAsync(x => x.Source == source)) throw new Exception("Deletion reconciliation failed");
dynamo.Fail = true;
try { await Import(); throw new Exception("Expected import failure"); } catch (InvalidOperationException) { }
db.ChangeTracker.Clear();
var failed = await db.ImportRuns.Where(x => x.Source == source).OrderByDescending(x => x.StartedAt).FirstAsync();
if (failed.Status != "failed" || failed.FinishedAt is null) throw new Exception("Failure was not recorded");
await db.ImportRuns.Where(x => x.Source == source).ExecuteDeleteAsync();
Console.WriteLine("Synthetic import, projection upgrade, repeat, update, deletion and failure passed.");

sealed class FakeDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
{
    public bool Empty;
    public bool Fail;
    public bool Deleted;
    public bool ApartmentApproved;
    public bool SkipApartmentInScan;
    public bool ApartmentRemoved;
    public const string ApartmentKey = "COMPLEX#test#OWNER_APARTMENTS#42";
    public string Serial = "old-serial";
    private static Dictionary<string, AttributeValue> Item(string pk, string sk) => new() { ["pk"] = new(pk), ["sk"] = new(sk) };
    private Dictionary<string, AttributeValue> OwnerApartmentItem()
    {
        var apartment = new OwnerApartment("apartment", "4Г", 12, DateTimeOffset.UnixEpoch.AddTicks(7));
        if (ApartmentApproved)
            apartment = apartment with
            {
                Status = ApartmentReviewStatus.Approved,
                ReviewedAt = DateTimeOffset.Parse("2026-01-03T02:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture),
                ReviewedBy = "reviewer@example.test"
            };
        return new OwnerApartments("test", 42, "ref", ApartmentApproved ? 2 : 1, [apartment]).Save("residents", null).Put.Item;
    }
    public override Task<GetItemResponse> GetItemAsync(GetItemRequest r, CancellationToken ct = default) =>
        Task.FromResult(new GetItemResponse { Item = !Empty && !ApartmentRemoved && r.Key.GetValueOrDefault("pk")?.S == ApartmentKey ? OwnerApartmentItem() : [] });
    public override Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest r, CancellationToken ct = default) => Task.FromResult(new DescribeTableResponse { Table = new TableDescription { TableArn = "arn:aws:dynamodb:eu-central-1:123456789012:table/" + r.TableName } });
    public override Task<ScanResponse> ScanAsync(ScanRequest r, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("Synthetic failure");
        var items = new List<Dictionary<string, AttributeValue>>();
        if (Empty) return Task.FromResult(new ScanResponse { Items = items });
        if (r.TableName.EndsWith("administrators")) items.Add(new() { ["UserId"] = new("admin"), ["NotifyNewRequests"] = new() { BOOL = true } });
        if (r.TableName.EndsWith("residents"))
        {
            if (!SkipApartmentInScan && !ApartmentRemoved) items.Add(OwnerApartmentItem());
            items.Add(new() { ["pk"] = new("COMPLEX#test#USER#42"), ["payload"] = new("{\"UserId\":42,\"BuildingId\":\"test\",\"Phone\":\"+380671234567\",\"ConsentAt\":\"2026-01-01T00:00:00Z\",\"ConsentVersion\":\"v1\",\"PersonalDataDeleted\":" + (Deleted ? "true" : "false") + "}") });
            items.Add(new() { ["pk"] = new("COMPLEX#test#RECORD#REGISTRATION#ref#EVENT#DECIDED"), ["sourcePk"] = new("REGISTRATION#ref"), ["sourceSk"] = new("EVENT#DECIDED"), ["recordData"] = new() { M = new() { ["actor"] = new("test") } } });
            items.Add(new() { ["pk"] = new("COMPLEX#test#RECORD#USER#42#MEMBERSHIP"), ["sourcePk"] = new("USER#42"), ["sourceSk"] = new("MEMBERSHIP"), ["complexId"] = new("test"), ["recordData"] = new() { M = new() { ["active"] = new() { BOOL = true } } } });
        }
        if (r.TableName.EndsWith("command-data"))
        {
            var request = Item("USER#42", "REQUEST#00000000000000000001");
            request["payload"] = new("{\"Id\":1,\"OwnerId\":42,\"BuildingId\":\"test\",\"Status\":3,\"CreatedAt\":\"2026-01-01T00:00:00Z\",\"KeyItems\":[{\"Type\":\"Магніт\",\"Phone\":\"+380671234567\",\"SerialNumber\":\"" + Serial + "\"}]}");
            items.Add(request);
            items.Add(Item("REQUEST#1", "EVENT#CREATED"));
            items.Add(Item("REGISTRATION#ref", "EVENT#DECIDED"));
            var message = Item("MESSAGES#test", "MESSAGE#2026-01-01#id");
            message["payload"] = new("{\"Id\":\"id\",\"Text\":\"test\",\"CreatedById\":\"admin\",\"CreatedBy\":\"admin\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}");
            items.Add(message);
            items.Add(Item("NOTIFICATIONS#test", "RESULT#id"));
            items.Add(Item("ADMIN#admin", "EVENT#id"));
        }
        if (r.TableName.EndsWith("statistics")) items.Add(Item("API", "2026-01-01#GET /admin/me"));
        return Task.FromResult(new ScanResponse { Items = items });
    }
}
