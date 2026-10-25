using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RequestHistoryTests
{
    private static readonly MaintenanceRequest Request = new(7, 99, "complex", "інше", "", "Опис заявки", DateTimeOffset.UnixEpoch);
    private static readonly ResidentRegistration Resident = new("reference", 99, "complex", RegistrationStage.Approved, 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "+380500000000", " Олена ", " Ковальчук ");
    private static Dictionary<string, AttributeValue> Created() => new()
    {
        ["pk"] = new("REQUEST#7"),
        ["sk"] = new("EVENT#CREATED"),
        ["actorId"] = new() { N = "99" },
        ["createdAt"] = new("2026-10-23T08:00:00Z"),
        ["status"] = new("New")
    };
    private static Dictionary<string, AttributeValue> Updated() => new()
    {
        ["pk"] = new("REQUEST#7"),
        ["sk"] = new("EVENT#UPDATED"),
        ["actor"] = new("admin@example.invalid"),
        ["createdAt"] = new("2026-10-23T09:00:00Z"),
        ["status"] = new("InProgress"),
        ["resolution"] = new("Працюємо")
    };

    [Fact]
    public async Task OldCreationEventResolvesOwnerNameWithoutModifyingAuditOrAdminActor()
    {
        using var db = new HistoryDynamo { Resident = Resident, Items = [Created(), Updated()] };
        var history = await new DynamoAdminStore(db, "admins", "data", "complex").HistoryAsync(Request, default);
        Assert.Equal(2, history.Count);
        Assert.Equal("admin@example.invalid", history[0]["actor"]);
        Assert.Equal("Працюємо", history[0]["resolution"]);
        Assert.Equal("Олена Ковальчук", history[1]["actor"]);
        Assert.Equal("Created", history[1]["action"]);
        Assert.Equal("New", history[1]["status"]);
        Assert.Equal("REQUEST#7", db.Query!.ExpressionAttributeValues[":pk"].S);
        Assert.Equal(100, db.Query.Limit);
        Assert.True(db.Query.ConsistentRead);
        Assert.Equal("USER#99", db.Get!.Key["pk"].S);
        Assert.Equal("REGISTRATION", db.Get.Key["sk"].S);
        Assert.True(db.Get.ConsistentRead);
        Assert.Single(db.Items, item => item.ContainsKey("actorId"));
        Assert.False(db.Items[0].ContainsKey("actor"));
        var json = JsonSerializer.Serialize(history);
        Assert.DoesNotContain("actorId", json);
        Assert.DoesNotContain(Resident.Phone!, json);
        Assert.DoesNotContain("payload", json);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-complex")]
    [InlineData("other-owner")]
    [InlineData("empty-name")]
    public async Task MissingOrForeignProfileCannotExposeResidentData(string profile)
    {
        using var db = new HistoryDynamo
        {
            Resident = profile switch
            {
                "missing" => null,
                "other-complex" => Resident with { BuildingId = "other" },
                "other-owner" => Resident with { UserId = 123 },
                _ => Resident with { FirstName = null, LastName = " " }
            },
            Items = [Created()]
        };
        var entry = Assert.Single(await new DynamoAdminStore(db, "admins", "data", "complex").HistoryAsync(Request, default));
        Assert.Equal("Мешканець", entry["actor"]);
        Assert.DoesNotContain("actorId", entry.Keys);
        Assert.DoesNotContain("phone", entry.Keys);
    }

    [Fact]
    public async Task PartialNameIsDisplayedWithoutBlankParts()
    {
        using var db = new HistoryDynamo { Resident = Resident with { LastName = null }, Items = [Created()] };
        var entry = Assert.Single(await new DynamoAdminStore(db, "admins", "data", "complex").HistoryAsync(Request, default));
        Assert.Equal("Олена", entry["actor"]);
    }

    [Fact]
    public async Task NoProfileLookupWhenCreationIsOutsideReturnedHistory()
    {
        using var db = new HistoryDynamo { Items = [Updated()] };
        await new DynamoAdminStore(db, "admins", "data", "complex").HistoryAsync(Request, default);
        Assert.Null(db.Get);
    }

    [Fact]
    public async Task ForeignComplexRequestIsRejectedBeforeReadingHistoryOrProfile()
    {
        using var db = new HistoryDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .HistoryAsync(Request with { BuildingId = "other" }, default));
        Assert.Equal(404, error.Status);
        Assert.Null(db.Query);
        Assert.Null(db.Get);
    }

    [Fact]
    public async Task RequestAuthorUsesPrivateProfileWithAddressAndDoesNotExposePhoneOrTelegramId()
    {
        using var db = new HistoryDynamo { Resident = Resident with { BuildingNumber = "4Д", ApartmentNumber = "125" } };
        var author = await new DynamoAdminStore(db, "admins", "data", "complex").GetRequestAuthorAsync(Request, default);
        Assert.Equal(new RequestAuthor("Олена", "Ковальчук", "4Д", "125"), author);
        Assert.Equal("USER#99", db.Get!.Key["pk"].S);
        Assert.True(db.Get.ConsistentRead);
        var json = JsonSerializer.Serialize(author);
        Assert.DoesNotContain(Resident.Phone!, json);
        Assert.DoesNotContain("UserId", json);
        Assert.DoesNotContain("Phone", json);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-complex")]
    [InlineData("other-owner")]
    public async Task RequestAuthorDoesNotExposeForeignOrMissingProfile(string kind)
    {
        using var db = new HistoryDynamo
        {
            Resident = kind switch
            {
                "missing" => null,
                "other-complex" => Resident with { BuildingId = "other" },
                _ => Resident with { UserId = 100 }
            }
        };
        Assert.Null(await new DynamoAdminStore(db, "admins", "data", "complex").GetRequestAuthorAsync(Request, default));
    }

    [Fact]
    public async Task RequestAuthorRejectsForeignRequestBeforeReadingProfile()
    {
        using var db = new HistoryDynamo { Resident = Resident };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .GetRequestAuthorAsync(Request with { BuildingId = "other" }, default));
        Assert.Equal(404, error.Status);
        Assert.Null(db.Get);
    }

    [Fact]
    public async Task OldResidentStillHasNameButNoInventedBuildingOrApartment()
    {
        using var db = new HistoryDynamo { Resident = Resident };
        var author = await new DynamoAdminStore(db, "admins", "data", "complex").GetRequestAuthorAsync(Request, default);
        Assert.Equal("Олена", author!.FirstName);
        Assert.Null(author.BuildingNumber);
        Assert.Null(author.ApartmentNumber);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletedDataLabelPersistsForOldRequestsEvenAfterProfileIsRestored(bool deleted)
    {
        using var db = new HistoryDynamo
        {
            Resident = Resident with
            {
                PersonalDataDeleted = deleted,
                PersonalDataDeletedAt = Request.CreatedAt.AddDays(1)
            },
            Items = [Created()]
        };
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var author = await store.GetRequestAuthorAsync(Request, default);
        Assert.True(author!.PersonalDataDeleted);
        Assert.Null(author.FirstName);
        Assert.Null(author.BuildingNumber);
        var entry = Assert.Single(await store.HistoryAsync(Request, default));
        Assert.Equal("Мешканець видалив свої дані", entry["actor"]);
    }

    [Fact]
    public async Task RequestsCreatedAfterProfileRestorationUseCurrentProfile()
    {
        using var db = new HistoryDynamo { Resident = Resident with { PersonalDataDeletedAt = Request.CreatedAt.AddDays(-1) } };
        var author = await new DynamoAdminStore(db, "admins", "data", "complex").GetRequestAuthorAsync(Request, default);
        Assert.False(author!.PersonalDataDeleted);
        Assert.Equal("Олена", author.FirstName);
    }

    private sealed class HistoryDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public ResidentRegistration? Resident;
        public List<Dictionary<string, AttributeValue>> Items = [];
        public QueryRequest? Query;
        public GetItemRequest? Get;
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        { Query = request; return Task.FromResult(new QueryResponse { Items = Items }); }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            Get = request;
            return Task.FromResult(new GetItemResponse { Item = Resident is null ? [] : new() { ["payload"] = new(JsonSerializer.Serialize(Resident)) } });
        }
    }
}
