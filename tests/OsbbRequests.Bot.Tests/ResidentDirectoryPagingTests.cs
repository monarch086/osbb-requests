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
    public async Task ApprovalAndHistoryUseMigratedRecords()
    {
        using var db = new RelatedDynamo();
        var store = new DynamoAdminStore(db, "admins", "old", "complex", residentsTable: "residents");
        var approval = await store.GetRegistrationApprovalAsync("reference", default);
        Assert.Equal("admin@example.org", approval!.Actor);
        Assert.True(db.GetRequest!.ConsistentRead);
        Assert.Equal("residents", db.GetRequest.TableName);
        var history = await store.RegistrationHistoryAsync("reference", null, default);
        Assert.Equal("admin@example.org", Assert.Single(history.Items).Actor);
        Assert.Equal("ResidentRecordsByGroup", db.QueryRequest!.IndexName);
        Assert.Equal("COMPLEX#complex#REGISTRATION#reference", db.QueryRequest.ExpressionAttributeValues[":group"].S);
    }

    private sealed class RelatedDynamo() : AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), RegionEndpoint.EUCentral1)
    {
        public GetItemRequest? GetRequest { get; private set; }
        public QueryRequest? QueryRequest { get; private set; }
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
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            QueryRequest = request;
            var entry = new ResidentTypeChange("FamilyMember", "Owner", "admin@example.org", DateTimeOffset.UnixEpoch, false, false);
            return Task.FromResult(new QueryResponse
            {
                Items = [new() { ["recordData"] = new() { M = new() {
                ["payload"] = new(JsonSerializer.Serialize(entry)) } } }]
            });
        }
    }

    private sealed class DirectoryDynamo(ResidentRegistration[] residents) : AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"), RegionEndpoint.EUCentral1)
    {
        public List<QueryRequest> Queries { get; } = [];
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Queries.Add(request);
            var second = request.ExclusiveStartKey is { Count: > 0 };
            var subset = second ? residents.Skip(15) : residents.Take(15);
            return Task.FromResult(new QueryResponse
            {
                Items = subset.Select(r => new Dictionary<string, AttributeValue> { ["payload"] = new(JsonSerializer.Serialize(r)) }).ToList(),
                LastEvaluatedKey = !second && residents.Length > 15 ? new() { ["pk"] = new("next") } : null
            });
        }
    }
}
