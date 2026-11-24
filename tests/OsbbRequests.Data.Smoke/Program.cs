using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OsbbRequests.Data;
using OsbbRequests.Data.Storage;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
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
var resident = await db.Residents.SingleAsync(x => x.Source == source);
if (resident.RegistrationId != "ref" || resident.Stage != RegistrationStage.Approved || resident.Version != 3 ||
    resident.FirstName != "Ім’я" || resident.LastName != "Прізвище" || resident.Email != "resident@example.test" ||
    resident.Phone != "+380671234567" || resident.BuildingNumber != "4Б" || resident.ApartmentNumber != "007" ||
    resident.ResidentType != ResidentType.Owner || resident.ConsentVersion != "v1" ||
    resident.ConsentAt != new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Resident profile columns were not imported");
var request = await db.Requests.SingleAsync(x => x.Source == source);
if (request.Status != RequestStatus.Completed || request.Visibility != RequestVisibility.Private ||
    request.Category != RequestCategories.KeyOrder || request.Description != "Тестове замовлення" ||
    request.Location != "" || request.PlannedDate != new DateOnly(2026, 1, 2) || request.AssignedTo != "admin" ||
    request.Priority != "High" || request.Resolution != "Готово" || request.Scope is not null ||
    request.CreatedAt != new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Request columns were not imported");
var apartment = await db.ResidentApartments.SingleAsync(x => x.Source == source);
if (apartment.RegistrationId != "ref" || apartment.BuildingNumber != "4Г" || apartment.ApartmentNumber != 12 ||
    apartment.Status != ApartmentReviewStatus.Pending || apartment.ReviewedAt is not null || apartment.ReviewedBy is not null ||
    await db.SourceRecords.AnyAsync(x => x.Source == source && x.Key == FakeDynamo.ApartmentKey + "\u001f"))
    throw new Exception("Relational apartment import failed");
var permissions = await db.AdministratorPermissions.SingleAsync(x => x.Source == source);
var settings = await db.AdministratorNotificationSettings.SingleAsync(x => x.Source == source);
var administrator = await db.Administrators.SingleAsync(x => x.Source == source);
if (administrator.Email != "admin@example.test" || administrator.DisplayName != "Адміністратор" ||
    !administrator.IsActive || administrator.Version != 1 || administrator.UpdatedAt is not null ||
    administrator.CreatedAt != new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Administrator columns were not imported");
var membership = await db.ResidentMemberships.SingleAsync(x => x.Source == source);
if (membership.ComplexId != "test" || membership.TelegramUserId != 42 || !membership.Active ||
    membership.RegistrationId != "ref" || membership.ReviewedBy != "system" ||
    membership.ReviewedAt != new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Membership columns were not imported");
if (!permissions.ComplexRequests || !permissions.KeyOrders || permissions.Messages || !settings.NewRequests || !settings.NewKeyOrders)
    throw new Exception("Legacy administrator defaults failed");
if (!settings.NewResidents || !settings.NewApartmentRegistrations)
    throw new Exception("Apartment notification fallback failed");
await Import();
var run = await db.ImportRuns.Where(x => x.Source == source).OrderByDescending(x => x.StartedAt).FirstAsync();
if (run.ChangedCount != 0 || run.Status != "succeeded") throw new Exception("Repeat failed");
dynamo.ApartmentNotifications = false;
dynamo.AccessRevoked = true;
await Import();
settings = await db.AdministratorNotificationSettings.SingleAsync(x => x.Source == source);
if (!settings.NewResidents || settings.NewApartmentRegistrations)
    throw new Exception("Explicit apartment notification setting was not imported");
administrator = await db.Administrators.SingleAsync(x => x.Source == source);
membership = await db.ResidentMemberships.SingleAsync(x => x.Source == source);
var reviewAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
if (administrator.IsActive || administrator.Version != 2 || administrator.UpdatedAt != reviewAt ||
    membership.Active || membership.ComplexId != "test" || membership.ReviewedBy != "admin@example.test" ||
    membership.ReviewedAt != reviewAt || membership.RegistrationId is not null)
    throw new Exception("Administrator or membership columns were not updated");
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
request = await db.Requests.SingleAsync(x => x.Source == source);
if (request.Status != RequestStatus.InProgress || request.PlannedDate is not null || request.Resolution is not null)
    throw new Exception("Request columns were not updated");
dynamo.Deleted = true;
await Import();
resident = await db.Residents.SingleAsync(x => x.Source == source);
if (!resident.PersonalDataDeleted || resident.Stage != RegistrationStage.Deleted || resident.Email is not null ||
    resident.Phone is not null || resident.FirstName is not null || resident.ConsentAt is not null)
    throw new Exception("Resident profile columns were not updated");
if (await db.ResidentPhones.AnyAsync(x => x.Source == source) || await db.ResidentConsents.AnyAsync(x => x.Source == source)) throw new Exception("Deleted profile retained personal data projections");
dynamo.Deleted = false;
await Import();
if (await db.ResidentPhones.CountAsync(x => x.Source == source) != 1 || await db.ResidentConsents.CountAsync(x => x.Source == source) != 1) throw new Exception("Restored profile missing projections");
dynamo.Empty = true;
await Import();
if (await db.SourceRecords.AnyAsync(x => x.Source == source) || await db.Residents.AnyAsync(x => x.Source == source) || await db.Requests.AnyAsync(x => x.Source == source) || await db.RequestKeyItems.AnyAsync(x => x.Source == source) ||
    await db.ResidentApartments.AnyAsync(x => x.Source == source) ||
    await db.RequestEvents.AnyAsync(x => x.Source == source) || await db.ResidentEvents.AnyAsync(x => x.Source == source) ||
    await db.ResidentPhones.AnyAsync(x => x.Source == source) || await db.ResidentConsents.AnyAsync(x => x.Source == source) ||
    await db.Administrators.AnyAsync(x => x.Source == source) || await db.ResidentMemberships.AnyAsync(x => x.Source == source) || await db.BroadcastMessages.AnyAsync(x => x.Source == source) ||
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
    public bool AccessRevoked;
    public bool ApartmentApproved;
    public bool SkipApartmentInScan;
    public bool ApartmentRemoved;
    public bool? ApartmentNotifications;
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
        if (r.TableName.EndsWith("administrators"))
        {
            var admin = new Dictionary<string, AttributeValue>
            {
                ["UserId"] = new("admin"),
                ["Email"] = new("admin@example.test"),
                ["DisplayName"] = new("Адміністратор"),
                ["CreatedAt"] = new("2026-01-01T02:00:00+02:00"),
                ["IsActive"] = new() { BOOL = !AccessRevoked },
                ["Version"] = new() { N = AccessRevoked ? "2" : "1" },
                ["NotifyNewRequests"] = new() { BOOL = true },
                ["NotifyNewResidents"] = new() { BOOL = true }
            };
            if (AccessRevoked) admin["UpdatedAt"] = new("2026-01-03T00:00:00Z");
            if (ApartmentNotifications is { } value) admin["NotifyNewApartmentRegistrations"] = new() { BOOL = value };
            items.Add(admin);
        }
        if (r.TableName.EndsWith("residents"))
        {
            if (!SkipApartmentInScan && !ApartmentRemoved) items.Add(OwnerApartmentItem());
            var profile = Deleted
                ? """{"Id":"ref","UserId":42,"BuildingId":"test","Stage":"Deleted","PersonalDataDeleted":true}"""
                : """
                  {"Id":"ref","UserId":42,"BuildingId":"test","Stage":"Approved","Version":3,
                   "Phone":"+380671234567","FirstName":"Ім’я","LastName":"Прізвище","Email":"resident@example.test",
                   "BuildingNumber":"4Б","ApartmentNumber":"007","ResidentType":"Owner",
                   "ConsentAt":"2026-01-01T02:00:00+02:00","ConsentVersion":"v1"}
                  """;
            items.Add(new() { ["pk"] = new("COMPLEX#test#USER#42"), ["payload"] = new(profile) });
            items.Add(new() { ["pk"] = new("COMPLEX#test#RECORD#REGISTRATION#ref#EVENT#DECIDED"), ["sourcePk"] = new("REGISTRATION#ref"), ["sourceSk"] = new("EVENT#DECIDED"), ["recordData"] = new() { M = new() { ["actor"] = new("test") } } });
            var membership = Item("USER#42", "MEMBERSHIP");
            membership["buildingId"] = new("test");
            membership["active"] = new() { BOOL = !AccessRevoked };
            membership["reviewedBy"] = new(AccessRevoked ? "admin@example.test" : "system");
            membership["reviewedAt"] = new(AccessRevoked ? "2026-01-03T00:00:00Z" : "2026-01-02T02:00:00+02:00");
            if (!AccessRevoked) membership["registrationId"] = new("ref");
            items.Add(new() { ["pk"] = new("COMPLEX#test#RECORD#USER#42#MEMBERSHIP"), ["sourcePk"] = new("USER#42"), ["sourceSk"] = new("MEMBERSHIP"), ["recordData"] = new() { M = membership } });
        }
        if (r.TableName.EndsWith("command-data"))
        {
            var request = Item("USER#42", "REQUEST#00000000000000000001");
            var status = Serial == "old-serial"
                ? "\"Status\":3,\"PlannedDate\":\"2026-01-02\",\"Resolution\":\"Готово\""
                : "\"Status\":\"InProgress\"";
            request["payload"] = new($$"""
                {"Id":1,"OwnerId":42,"BuildingId":"test",{{status}},"CreatedAt":"2026-01-01T02:00:00+02:00",
                 "Category":"Замовлення ключів","Location":"","Description":"Тестове замовлення","AssignedTo":"admin","Priority":"High",
                 "KeyItems":[{"Type":"Магніт","Phone":"+380671234567","SerialNumber":"{{Serial}}"}]}
                """);
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
