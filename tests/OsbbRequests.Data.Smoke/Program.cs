using System.Text.Json;
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
var sourceId = await db.ImportSources.Where(x => x.AwsAccountId == "123456789012" && x.Region == "eu-central-1" && x.Stage == stage)
    .Select(x => x.Id).SingleAsync();
var complexId = await db.Complexes.Where(x => x.SourceId == sourceId && x.ExternalId == "test").Select(x => x.Id).SingleAsync();
var residentId = await db.Residents.Where(x => x.ComplexId == complexId).Select(x => x.Id).SingleAsync();
if (await db.Residents.CountAsync(x => x.ComplexId == complexId) != 1 || await db.Requests.CountAsync(x => x.SourceId == sourceId) != 1 ||
    await db.Administrators.CountAsync(x => x.SourceId == sourceId) != 1 || await db.RequestKeyItems.CountAsync(x => x.SourceId == sourceId) != 1 ||
    await db.RequestEvents.CountAsync(x => x.SourceId == sourceId) != 1 || await db.ResidentEvents.CountAsync(x => x.SourceId == sourceId) != 1 ||
    await db.ResidentPhones.CountAsync(x => x.ResidentId == residentId) != 1 || await db.ResidentConsents.CountAsync(x => x.ResidentId == residentId) != 1 ||
    await db.ResidentMemberships.CountAsync(x => x.SourceId == sourceId) != 1 || await db.BroadcastMessages.CountAsync(x => x.SourceId == sourceId) != 1 ||
    await db.Notifications.CountAsync(x => x.SourceId == sourceId) != 1 || await db.AdministratorEvents.CountAsync(x => x.SourceId == sourceId) != 1 ||
    await db.AdministratorPermissions.CountAsync(x => x.SourceId == sourceId) != 1 || await db.AdministratorNotificationSettings.CountAsync(x => x.SourceId == sourceId) != 1 ||
    await db.ApiDailyStatistics.CountAsync(x => x.SourceId == sourceId) != 1) throw new Exception("Projection failed");
var resident = await db.Residents.SingleAsync(x => x.ComplexId == complexId);
if (resident.RegistrationId != "ref" || resident.Stage != RegistrationStage.Approved || resident.Version != 3 ||
    resident.FirstName != "Ім’я" || resident.LastName != "Прізвище" || resident.Email != "resident@example.test" ||
    resident.BuildingNumber != "4Б" || resident.ApartmentNumber != "007" || resident.ResidentType != ResidentType.Owner)
    throw new Exception("Resident profile columns were not imported");
var primaryPhone = await db.ResidentPhones.SingleAsync(x => x.ResidentId == residentId);
var consent = await db.ResidentConsents.SingleAsync(x => x.ResidentId == residentId);
if (!primaryPhone.IsPrimary || primaryPhone.Phone != "+380671234567" || consent.Version != "v1" ||
    consent.ConsentedAt != new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Resident phone or consent was not imported");
var statisticDate = new DateOnly(2026, 1, 1);
const string statisticEndpoint = "GET /admin/me";
var statistic = await db.ApiDailyStatistics.FindAsync(sourceId, statisticDate, statisticEndpoint);
if (statistic is null || statistic.Requests != 4 ||
    !await db.SourceRecords.AnyAsync(x => x.SourceId == sourceId && x.Table == "statistics" && x.Key == "API\u001f2026-01-01#GET /admin/me"))
    throw new Exception("Daily statistic or its DynamoDB key was not imported");
var request = await db.Requests.SingleAsync(x => x.SourceId == sourceId);
if (request.ComplexId != complexId || request.Status != RequestStatus.Completed || request.Visibility != RequestVisibility.Private || request.Version != 3 ||
    request.Category != RequestCategories.KeyOrder || request.Description != "Тестове замовлення" ||
    request.Location != "" || request.PlannedDate != new DateOnly(2026, 1, 2) || request.AssignedTo != "admin" ||
    request.Priority != "High" || request.Resolution != "Готово" || request.Scope is not null ||
    request.CreatedAt != new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Request columns were not imported");
var apartment = await db.ResidentApartments.SingleAsync(x => x.ComplexId == complexId);
if (apartment.ResidentId != residentId || apartment.RegistrationId != "ref" || apartment.BuildingNumber != "4Г" || apartment.ApartmentNumber != 12 ||
    apartment.Status != ApartmentReviewStatus.Pending || apartment.ReviewedAt is not null || apartment.ReviewedBy is not null ||
    await db.SourceRecords.AnyAsync(x => x.SourceId == sourceId && x.Key == FakeDynamo.ApartmentKey + "\u001f"))
    throw new Exception("Relational apartment import failed");
var permissions = await db.AdministratorPermissions.SingleAsync(x => x.SourceId == sourceId);
var settings = await db.AdministratorNotificationSettings.SingleAsync(x => x.SourceId == sourceId);
var administrator = await db.Administrators.SingleAsync(x => x.SourceId == sourceId);
if (administrator.Email != "admin@example.test" || administrator.DisplayName != "Адміністратор" ||
    !administrator.IsActive || administrator.Version != 1 || administrator.UpdatedAt is not null ||
    administrator.CreatedAt != new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Administrator columns were not imported");
var membership = await db.ResidentMemberships.SingleAsync(x => x.SourceId == sourceId);
if (membership.ResidentId != residentId || membership.ComplexId != complexId || membership.TelegramUserId != 42 || !membership.Active ||
    membership.RegistrationId != "ref" || membership.ReviewedBy != "system" ||
    membership.ReviewedAt != new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))
    throw new Exception("Membership columns were not imported");
if (!permissions.ComplexRequests || !permissions.KeyOrders || permissions.Messages || !settings.NewRequests || !settings.NewKeyOrders)
    throw new Exception("Legacy administrator defaults failed");
if (!settings.NewResidents || !settings.NewApartmentRegistrations)
    throw new Exception("Apartment notification fallback failed");
await Import();
var run = await db.ImportRuns.Where(x => x.SourceId == sourceId).OrderByDescending(x => x.StartedAt).FirstAsync();
if (run.ChangedCount != 0 || run.Status != "succeeded" || await db.ImportRuns.CountAsync(x => x.SourceId == sourceId) != 2)
    throw new Exception("Repeat did not reuse the verified import source");
dynamo.ApartmentNotifications = false;
dynamo.AccessRevoked = true;
await Import();
settings = await db.AdministratorNotificationSettings.SingleAsync(x => x.SourceId == sourceId);
if (!settings.NewResidents || settings.NewApartmentRegistrations)
    throw new Exception("Explicit apartment notification setting was not imported");
administrator = await db.Administrators.SingleAsync(x => x.SourceId == sourceId);
membership = await db.ResidentMemberships.SingleAsync(x => x.SourceId == sourceId);
var reviewAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
if (administrator.IsActive || administrator.Version != 2 || administrator.UpdatedAt != reviewAt ||
    membership.Active || membership.ComplexId != complexId || membership.ReviewedBy != "admin@example.test" ||
    membership.ReviewedAt != reviewAt || membership.RegistrationId is not null)
    throw new Exception("Administrator or membership columns were not updated");
db.SourceRecords.Add(new SourceRecord
{
    SourceId = sourceId,
    Table = "residents",
    Key = FakeDynamo.ApartmentKey + "\u001f",
    Data = "{\"payload\":\"legacy\"}",
    Hash = "legacy",
    LastSeenAt = DateTimeOffset.UtcNow
});
await db.SaveChangesAsync();
await Import();
if (await db.SourceRecords.AnyAsync(x => x.SourceId == sourceId && x.Key == FakeDynamo.ApartmentKey + "\u001f"))
    throw new Exception("Legacy JSON apartment copy survived import");
dynamo.ApartmentApproved = true;
await Import();
apartment = await db.ResidentApartments.SingleAsync(x => x.ComplexId == complexId);
if (apartment.Status != ApartmentReviewStatus.Approved || apartment.ReviewedBy != "reviewer@example.test" ||
    apartment.ReviewedAt != DateTimeOffset.Parse("2026-01-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
    throw new Exception("Apartment decision was not updated");
dynamo.SkipApartmentInScan = true;
await Import();
if (!await db.ResidentApartments.AnyAsync(x => x.ComplexId == complexId)) throw new Exception("Unseen but existing apartment record was deleted");
dynamo.ApartmentRemoved = true;
await Import();
if (await db.ResidentApartments.AnyAsync(x => x.ComplexId == complexId)) throw new Exception("Deleted apartment record remained");
dynamo.SkipApartmentInScan = false;
dynamo.ApartmentRemoved = false;
await db.SourceRecords.Where(x => x.SourceId == sourceId).ExecuteUpdateAsync(x => x.SetProperty(row => row.ProjectionVersion, 0));
await db.RequestEvents.Where(x => x.SourceId == sourceId).ExecuteDeleteAsync();
await Import();
if (await db.RequestEvents.CountAsync(x => x.SourceId == sourceId) != 1) throw new Exception("Projection upgrade failed");
dynamo.Serial = "new-serial";
await Import();
var key = await db.RequestKeyItems.SingleAsync(x => x.SourceId == sourceId);
if (key.SerialNumber != "new-serial" || key.Type != "Перепустка") throw new Exception("Key update failed");
request = await db.Requests.SingleAsync(x => x.SourceId == sourceId);
if (request.Status != RequestStatus.InProgress || request.PlannedDate is not null || request.Resolution is not null || request.Version != 4)
    throw new Exception("Request columns were not updated");
var keyEdit = await db.ResidentEvents.SingleAsync(x => x.SourceId == sourceId && x.KeyRequestId == 1);
if (keyEdit.RegistrationId != "ref" || keyEdit.Actor != "admin@example.test" ||
    keyEdit.CreatedAt != DateTimeOffset.Parse("2026-01-03T00:00:00Z") ||
    keyEdit.PreviousKeyType != "Магніт" || keyEdit.PreviousKeySerialNumber != "old-serial" ||
    keyEdit.CurrentKeyType != "Перепустка" || keyEdit.CurrentKeySerialNumber != "new-serial")
    throw new Exception("Key edit history was not projected");
db.ResidentPhones.Add(new ResidentPhone { ResidentId = residentId, Phone = "+380671234568" });
db.ResidentPhones.Add(new ResidentPhone { ResidentId = residentId, Phone = "+380671234569" });
await db.SaveChangesAsync();
dynamo.PrimaryPhone = "+380671234568";
dynamo.StatisticRequests = "8";
await Import();
var phones = await db.ResidentPhones.Where(x => x.ResidentId == residentId).ToListAsync();
statistic = await db.ApiDailyStatistics.FindAsync(sourceId, statisticDate, statisticEndpoint);
if (phones.Count != 2 || phones.Single(x => x.IsPrimary).Phone != "+380671234568" ||
    !phones.Any(x => x.Phone == "+380671234569" && !x.IsPrimary) || statistic?.Requests != 8 ||
    (await db.RequestKeyItems.SingleAsync(x => x.SourceId == sourceId)).Phone != "+380671234567")
    throw new Exception("Primary phone or statistic update was incorrect");
dynamo.Deleted = true;
await Import();
resident = await db.Residents.SingleAsync(x => x.ComplexId == complexId);
if (!resident.PersonalDataDeleted || resident.Stage != RegistrationStage.Deleted || resident.Email is not null ||
    resident.FirstName is not null)
    throw new Exception("Resident profile columns were not updated");
if (await db.ResidentPhones.AnyAsync(x => x.ResidentId == residentId) || await db.ResidentConsents.AnyAsync(x => x.ResidentId == residentId)) throw new Exception("Deleted profile retained personal data projections");
dynamo.Deleted = false;
dynamo.AwaitingConsent = true;
await Import();
consent = await db.ResidentConsents.SingleAsync(x => x.ResidentId == residentId);
if (consent.ConsentedAt is not null || consent.Version != "v2" || await db.ResidentPhones.AnyAsync(x => x.ResidentId == residentId))
    throw new Exception("A pending consent was recorded as granted or retained old contact information");
dynamo.AwaitingConsent = false;
await Import();
if (await db.ResidentPhones.CountAsync(x => x.ResidentId == residentId) != 1 || await db.ResidentConsents.CountAsync(x => x.ResidentId == residentId) != 1) throw new Exception("Restored profile missing projections");
dynamo.SkipProfile = true;
await Import();
if (await db.Residents.AnyAsync(x => x.ComplexId == complexId) ||
    await db.ResidentPhones.AnyAsync(x => x.ResidentId == residentId) ||
    await db.ResidentConsents.AnyAsync(x => x.ResidentId == residentId) ||
    (await db.ResidentApartments.SingleAsync(x => x.ComplexId == complexId)).ResidentId is not null ||
    (await db.ResidentMemberships.SingleAsync(x => x.SourceId == sourceId)).ResidentId is not null)
    throw new Exception("Removing a profile failed to unlink its historical records");

dynamo.SkipProfile = false;
await Import();
residentId = await db.Residents.Where(x => x.ComplexId == complexId).Select(x => x.Id).SingleAsync();
if ((await db.ResidentApartments.SingleAsync(x => x.ComplexId == complexId)).ResidentId != residentId ||
    (await db.ResidentMemberships.SingleAsync(x => x.SourceId == sourceId)).ResidentId != residentId)
    throw new Exception("Import did not link a newly available profile");

dynamo.KeyRemoved = true;
await Import();
request = await db.Requests.SingleAsync(x => x.SourceId == sourceId);
var keyDeletion = await db.ResidentEvents.SingleAsync(x => x.SourceId == sourceId && x.KeyRequestId == 1 && x.CurrentKeyType == null);
if (request.Version != 5 || await db.RequestKeyItems.AnyAsync(x => x.SourceId == sourceId) ||
    keyDeletion.PreviousKeyType != "Перепустка" || keyDeletion.PreviousKeySerialNumber != "new-serial" ||
    keyDeletion.CurrentKeySerialNumber is not null)
    throw new Exception("Key removal or its history was not projected");

dynamo.SkipRequest = true;
await Import();
if (await db.Requests.AnyAsync(x => x.SourceId == sourceId) ||
    await db.RequestKeyItems.AnyAsync(x => x.SourceId == sourceId) ||
    !await db.RequestEvents.AnyAsync(x => x.SourceId == sourceId && x.RequestId == 1) ||
    await db.ResidentEvents.CountAsync(x => x.SourceId == sourceId && x.KeyRequestId == 1) != 2)
    throw new Exception("Removing a request failed to preserve its independently imported history");

dynamo.Empty = true;
await Import();
if (await db.SourceRecords.AnyAsync(x => x.SourceId == sourceId) || await db.Residents.AnyAsync(x => x.ComplexId == complexId) || await db.Requests.AnyAsync(x => x.SourceId == sourceId) || await db.RequestKeyItems.AnyAsync(x => x.SourceId == sourceId) ||
    await db.ResidentApartments.AnyAsync(x => x.ComplexId == complexId) ||
    await db.RequestEvents.AnyAsync(x => x.SourceId == sourceId) || await db.ResidentEvents.AnyAsync(x => x.SourceId == sourceId) ||
    await db.ResidentPhones.AnyAsync(x => x.ResidentId == residentId) || await db.ResidentConsents.AnyAsync(x => x.ResidentId == residentId) ||
    await db.Administrators.AnyAsync(x => x.SourceId == sourceId) || await db.ResidentMemberships.AnyAsync(x => x.SourceId == sourceId) || await db.BroadcastMessages.AnyAsync(x => x.SourceId == sourceId) ||
    await db.Notifications.AnyAsync(x => x.SourceId == sourceId) || await db.AdministratorEvents.AnyAsync(x => x.SourceId == sourceId) ||
    await db.AdministratorPermissions.AnyAsync(x => x.SourceId == sourceId) || await db.AdministratorNotificationSettings.AnyAsync(x => x.SourceId == sourceId) ||
    await db.ApiDailyStatistics.AnyAsync(x => x.SourceId == sourceId)) throw new Exception("Deletion reconciliation failed");
dynamo.Fail = true;
try { await Import(); throw new Exception("Expected import failure"); } catch (InvalidOperationException) { }
db.ChangeTracker.Clear();
var failed = await db.ImportRuns.Where(x => x.SourceId == sourceId).OrderByDescending(x => x.StartedAt).FirstAsync();
if (failed.Status != "failed" || failed.FinishedAt is null) throw new Exception("Failure was not recorded");
dynamo.FailDescribe = true;
try { await Import(); throw new Exception("Expected source discovery failure"); } catch (InvalidOperationException) { }
var unverifiedId = await db.ImportSources.Where(x => x.AwsAccountId == null && x.Region == "eu-central-1" && x.Stage == stage)
    .Select(x => x.Id).SingleAsync();
var unverified = await db.ImportRuns.SingleAsync(x => x.SourceId == unverifiedId);
if (unverified.Status != "failed" || unverified.FinishedAt is null) throw new Exception("Source discovery failure was not recorded");
await db.ImportRuns.Where(x => x.SourceId == sourceId || x.SourceId == unverifiedId).ExecuteDeleteAsync();
await db.Complexes.Where(x => x.SourceId == sourceId).ExecuteDeleteAsync();
await db.ImportSources.Where(x => x.Id == sourceId || x.Id == unverifiedId).ExecuteDeleteAsync();
Console.WriteLine("Synthetic import, projection upgrade, repeat, update, deletion and failure passed.");

sealed class FakeDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
{
    public bool Empty;
    public bool SkipRequest;
    public bool SkipProfile;
    public bool Fail;
    public bool FailDescribe;
    public bool Deleted;
    public bool AwaitingConsent;
    public string PrimaryPhone = "+380671234567";
    public string StatisticRequests = "4";
    public bool AccessRevoked;
    public bool ApartmentApproved;
    public bool SkipApartmentInScan;
    public bool ApartmentRemoved;
    public bool? ApartmentNotifications;
    public const string ApartmentKey = "COMPLEX#test#OWNER_APARTMENTS#42";
    public string Serial = "old-serial";
    public bool KeyRemoved;
    private static Dictionary<string, AttributeValue> Item(string pk, string sk) => new() { ["pk"] = new(pk), ["sk"] = new(sk) };

    private static Dictionary<string, AttributeValue> KeyHistoryItem(string timestamp, bool deleted)
    {
        var eventKey = $"EVENT#TYPE#{timestamp}";
        var payload = JsonSerializer.Serialize(new
        {
            Actor = "admin@example.test",
            CreatedAt = timestamp,
            KeyChange = new
            {
                RequestId = 1,
                Previous = deleted ? new { Type = "Перепустка", SerialNumber = "new-serial" } : new { Type = "Магніт", SerialNumber = "old-serial" },
                Current = deleted ? null : new { Type = "Перепустка", SerialNumber = "new-serial" }
            }
        });
        return new()
        {
            ["pk"] = new($"COMPLEX#test#RECORD#REGISTRATION#ref#{eventKey}"),
            ["sourcePk"] = new("REGISTRATION#ref"),
            ["sourceSk"] = new(eventKey),
            ["recordData"] = new() { M = new() { ["payload"] = new(payload) } }
        };
    }

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
    public override Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest r, CancellationToken ct = default) => FailDescribe
        ? Task.FromException<DescribeTableResponse>(new InvalidOperationException("Synthetic DescribeTable failure"))
        : Task.FromResult(new DescribeTableResponse { Table = new TableDescription { TableArn = "arn:aws:dynamodb:eu-central-1:123456789012:table/" + r.TableName } });
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
                : AwaitingConsent
                    ? """{"Id":"ref","UserId":42,"BuildingId":"test","Stage":"AwaitingConsent","ConsentVersion":"v2","PersonalDataDeletedAt":"2026-01-01T00:00:00Z"}"""
                    : $$"""
                  {"Id":"ref","UserId":42,"BuildingId":"test","Stage":"Approved","Version":3,
                   "Phone":"{{PrimaryPhone}}","FirstName":"Ім’я","LastName":"Прізвище","Email":"resident@example.test",
                   "BuildingNumber":"4Б","ApartmentNumber":"007","ResidentType":"Owner",
                   "ConsentAt":"2026-01-01T02:00:00+02:00","ConsentVersion":"v1"}
                  """;
            if (!SkipProfile) items.Add(new() { ["pk"] = new("COMPLEX#test#USER#42"), ["payload"] = new(profile) });
            items.Add(new() { ["pk"] = new("COMPLEX#test#RECORD#REGISTRATION#ref#EVENT#DECIDED"), ["sourcePk"] = new("REGISTRATION#ref"), ["sourceSk"] = new("EVENT#DECIDED"), ["recordData"] = new() { M = new() { ["actor"] = new("test") } } });
            if (Serial != "old-serial") items.Add(KeyHistoryItem("2026-01-03T00:00:00Z", deleted: false));
            if (KeyRemoved) items.Add(KeyHistoryItem("2026-01-04T00:00:00Z", deleted: true));
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
            var keyItems = KeyRemoved ? "[]" : JsonSerializer.Serialize(new[]
            {
                new { Type = Serial == "old-serial" ? "Магніт" : "Перепустка", Phone = "+380671234567", SerialNumber = Serial }
            });
            request["version"] = new() { N = KeyRemoved ? "5" : Serial == "old-serial" ? "3" : "4" };
            request["payload"] = new($$"""
                {"Id":1,"OwnerId":42,"BuildingId":"test",{{status}},"CreatedAt":"2026-01-01T02:00:00+02:00",
                 "Category":"Замовлення ключів","Location":"","Description":"Тестове замовлення","AssignedTo":"admin","Priority":"High",
                 "KeyItems":{{keyItems}}}
                """);
            if (!SkipRequest) items.Add(request);
            items.Add(Item("REQUEST#1", "EVENT#CREATED"));
            items.Add(Item("REGISTRATION#ref", "EVENT#DECIDED"));
            var message = Item("MESSAGES#test", "MESSAGE#2026-01-01#id");
            message["payload"] = new("{\"Id\":\"id\",\"Text\":\"test\",\"CreatedById\":\"admin\",\"CreatedBy\":\"admin\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}");
            items.Add(message);
            items.Add(Item("NOTIFICATIONS#test", "RESULT#id"));
            items.Add(Item("ADMIN#admin", "EVENT#id"));
        }
        if (r.TableName.EndsWith("statistics"))
        {
            var statistic = Item("API", "2026-01-01#GET /admin/me");
            statistic["requests"] = new() { N = StatisticRequests };
            items.Add(statistic);
        }
        return Task.FromResult(new ScanResponse { Items = items });
    }
}
