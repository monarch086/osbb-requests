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

public sealed class ResidentDirectoryPagingTests
{
    private static ResidentRegistration Resident(int id, string last, string? house = "4Б", ResidentType type = ResidentType.FamilyMember) =>
        new(id.ToString("D32"), id, "complex", RegistrationStage.Approved, 1, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(id), "+380500000000", "Ім'я", last, BuildingNumber: house, ResidentType: type);

    [Fact]
    public async Task FiltersAndUkrainianOrderingApplyBeforePagingAcrossAllIndexPages()
    {
        var residents = Enumerable.Range(1, 30).Select(i => Resident(i, $"Тест{i:D2}"))
            .Append(Resident(31, "Smith"))
            .Append(Resident(32, "Андрійчук"))
            .Append(Resident(33, "Інший", "4В"))
            .Append(Resident(34, "Інший", "4Б", ResidentType.Owner)).ToArray();
        using var db = new DirectoryDynamo(residents);
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var first = await store.ListResidentDirectoryAsync(false, 1, "4Б", "FamilyMember", "lastName", "asc", default);
        var second = await store.ListResidentDirectoryAsync(false, 2, "4Б", "FamilyMember", "lastName", "asc", default);
        Assert.Equal(32, first.Total);
        Assert.Equal(new ResidentCounts(33, 1, 32, 0, 0), first.Counts);
        Assert.Equal(first.Counts, second.Counts);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(7, second.Items.Count);
        Assert.Equal("Андрійчук", first.Items[0].LastName);
        Assert.Equal("Smith", second.Items[^1].LastName);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        var exported = await store.ListResidentExportAsync(false, "4Б", "FamilyMember", "lastName", "asc", default);
        Assert.Equal(first.Items.Concat(second.Items).Select(item => item.Id), exported.Select(item => item.Id));
        Assert.All(db.Queries, request => { Assert.Equal("residents", request.TableName); Assert.Equal("ResidentsByDate", request.IndexName); });
        Assert.True(db.Queries.Count >= 4);
    }

    [Fact]
    public async Task MissingHouseAndTypeFilterAndInvalidParametersAreHandled()
    {
        using var db = new DirectoryDynamo([Resident(1, "А", null), Resident(2, "Б", "4Б")]);
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var page = await store.ListResidentDirectoryAsync(false, 1, "Unspecified", "FamilyMember", "registeredAt", "desc", default);
        Assert.Equal(1, page.Total);
        Assert.Equal(new ResidentCounts(1, 0, 1, 0, 0), page.Counts);
        Assert.Equal(1, Assert.Single(page.Items).UserId);
        await Assert.ThrowsAsync<ApiError>(() => store.ListResidentDirectoryAsync(false, 0, null, null, null, null, default));
        await Assert.ThrowsAsync<ApiError>(() => store.ListResidentDirectoryAsync(false, 1, "other", null, null, null, default));
    }

    [Fact]
    public async Task PendingExportIncludesAllMatchingHousesAcrossIndexPages()
    {
        var residents = Enumerable.Range(1, 40).Select(i => Resident(i, "Ім’я", i <= 30 ? "4Б" : "4В") with { Stage = RegistrationStage.Pending }).ToArray();
        using var db = new DirectoryDynamo(residents);
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var result = await store.ListResidentExportAsync(true, "4Б", null, null, null, default);
        Assert.Equal(30, result.Count);
        Assert.All(result, r => Assert.Equal("4Б", r.BuildingNumber));
        Assert.Equal(2, db.Queries.Count);
    }

    [Fact]
    public async Task PendingStatisticsRespectBuildingAndIncludeAllResidentTypesAcrossIndexPages()
    {
        var residents = Enumerable.Range(1, 30).Select(i => Resident(i, "Тест", i <= 28 ? "4Б" : "4В",
            (i % 4) switch { 0 => ResidentType.Owner, 1 => ResidentType.FamilyMember, 2 => ResidentType.Tenant, _ => ResidentType.Employee })
            with
        { Stage = RegistrationStage.Pending }).ToArray();
        using var db = new DirectoryDynamo(residents);
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var page = await store.ListResidentDirectoryAsync(true, 1, "4Б", "Owner", null, null, default);
        Assert.Equal(new ResidentCounts(28, 7, 7, 7, 7), page.Counts);
        Assert.Equal(7, page.Total);
        Assert.All(page.Items, item => Assert.Equal(ResidentType.Owner, item.ResidentType));
        Assert.Equal(2, db.Queries.Count);
    }

    [Fact]
    public async Task LegacyProfilesWithoutTypeCountAsFamilyMembers()
    {
        using var db = new DirectoryDynamo([Resident(1, "Тест")], omitResidentType: true);
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var page = await store.ListResidentDirectoryAsync(false, 1, null, null, null, null, default);
        Assert.Equal(new ResidentCounts(1, 0, 1, 0, 0), page.Counts);
    }

    [Fact]
    public async Task ApprovalUsesMigratedRecords()
    {
        using var db = new RelatedDynamo();
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var approval = await store.GetRegistrationApprovalAsync("reference", default);
        Assert.Equal("admin@example.org", approval!.Actor);
        Assert.True(db.GetRequest!.ConsistentRead);
        Assert.Equal("residents", db.GetRequest.TableName);
    }

    [Fact]
    public async Task CompactHistoryResumesOldCursorAndPreservesOrderAcrossBatchRetries()
    {
        using var db = new CompactHistoryDynamo();
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var original = new Dictionary<string, string>
        {
            ["pk"] = CompactHistoryDynamo.Prefix + "3",
            ["recordGroup"] = CompactHistoryDynamo.Group,
            ["recordOrder"] = "EVENT#TYPE#3"
        };
        var cursor = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(original));
        var page = await store.RegistrationHistoryAsync("reference", cursor, default);

        Assert.Equal("ResidentRecordKeysByGroup", db.Query!.IndexName);
        Assert.Equal(original["recordGroup"], db.Query.ExclusiveStartKey["recordIndexGroup"].S);
        Assert.False(db.Query.ExclusiveStartKey.ContainsKey("recordGroup"));
        Assert.Equal(["second", "first"], page.Items.Select(item => item.Actor));
        Assert.Equal(2, db.BatchRequests.Count);
        Assert.All(db.BatchRequests, request => Assert.True(request.RequestItems["residents"].ConsistentRead));
        Assert.Single(db.BatchRequests[1].RequestItems["residents"].Keys);
        var outgoing = JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(page.Cursor!))!;
        Assert.Equal(original["recordGroup"], outgoing["recordGroup"]);
        Assert.False(outgoing.ContainsKey("recordIndexGroup"));
    }

    private sealed class CompactHistoryDynamo() : AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), RegionEndpoint.EUCentral1)
    {
        public const string Group = "COMPLEX#complex#REGISTRATION#reference";
        public const string Prefix = "COMPLEX#complex#RECORD#REGISTRATION#reference#EVENT#TYPE#";
        public QueryRequest? Query { get; private set; }
        public List<BatchGetItemRequest> BatchRequests { get; } = [];

        private static Dictionary<string, AttributeValue> IndexRow(string suffix) => new()
        {
            ["pk"] = new(Prefix + suffix),
            ["recordOrder"] = new("EVENT#TYPE#" + suffix),
            ["recordIndexGroup"] = new(Group)
        };

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Query = request;
            return Task.FromResult(new QueryResponse { Items = [IndexRow("2"), IndexRow("1")], LastEvaluatedKey = IndexRow("1") });
        }

        public override Task<BatchGetItemResponse> BatchGetItemAsync(BatchGetItemRequest request, CancellationToken cancellationToken = default)
        {
            BatchRequests.Add(request);
            var firstResponse = BatchRequests.Count == 1;
            var history = new ResidentTypeChange("FamilyMember", "Owner", firstResponse ? "first" : "second", DateTimeOffset.UnixEpoch, false, false);
            var row = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new(Prefix + (firstResponse ? "1" : "2")),
                ["recordData"] = new() { M = new() { ["payload"] = new(JsonSerializer.Serialize(history)) } }
            };
            return Task.FromResult(new BatchGetItemResponse
            {
                Responses = new() { ["residents"] = [row] },
                UnprocessedKeys = firstResponse ? new() { ["residents"] = new() { Keys = [request.RequestItems["residents"].Keys[0]] } } : []
            });
        }
    }

    private sealed class RelatedDynamo() : AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), RegionEndpoint.EUCentral1)
    {
        public GetItemRequest? GetRequest { get; private set; }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            GetRequest = request;
            return Task.FromResult(new GetItemResponse
            {
                Item = new()
                {
                    ["recordData"] = new()
                    {
                        M = new()
                        {
                            ["decision"] = new("Approved"),
                            ["actor"] = new("admin@example.org")
                        }
                    }
                }
            });
        }
    }

    private sealed class DirectoryDynamo(ResidentRegistration[] residents, bool omitResidentType = false) : AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), RegionEndpoint.EUCentral1)
    {
        public List<QueryRequest> Queries { get; } = [];
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Queries.Add(request);
            var second = request.ExclusiveStartKey is { Count: > 0 };
            var subset = second ? residents.Skip(15) : residents.Take(15);
            string Payload(ResidentRegistration resident)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(resident))!.AsObject();
                if (omitResidentType) node.Remove("ResidentType");
                return node.ToJsonString();
            }
            return Task.FromResult(new QueryResponse
            {
                Items = subset.Select(r => new Dictionary<string, AttributeValue> { ["payload"] = new(Payload(r)) }).ToList(),
                LastEvaluatedKey = !second && residents.Length > 15 ? new() { ["pk"] = new("next") } : null
            });
        }
    }
}
