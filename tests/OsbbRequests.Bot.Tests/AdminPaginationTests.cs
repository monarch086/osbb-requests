using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class AdminPaginationTests
{
    [Theory]
    [InlineData("requests")]
    [InlineData("residents")]
    [InlineData("registrations")]
    [InlineData("administrators")]
    public async Task ListsReturnAtMost25ItemsAndTraverseAllCursorsWithoutDuplicates(string list)
    {
        using var db = new PagedDynamo(list);
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var ids = new List<string>();
        string? cursor = null;
        foreach (var expectedCount in new[] { 25, 25, 1 })
        {
            IReadOnlyList<string> pageIds;
            if (list == "requests")
            {
                var page = await store.ListRequestsAsync(new(null, null, null, null, null), cursor, default);
                pageIds = page.Items.Select(i => i.Request.Id.ToString(CultureInfo.InvariantCulture)).ToList();
                cursor = page.Cursor;
            }
            else if (list == "residents")
            {
                var page = await store.ListResidentsAsync(cursor, default);
                pageIds = page.Items.Select(i => i.Id).ToList();
                cursor = page.Cursor;
            }
            else if (list == "registrations")
            {
                var page = await store.ListRegistrationsAsync(cursor, default);
                pageIds = page.Items.Select(i => i.Id).ToList();
                cursor = page.Cursor;
            }
            else
            {
                var page = await store.ListAdminsAsync(cursor, default);
                pageIds = page.Items.Select(i => i.UserId).ToList();
                cursor = page.Cursor;
            }
            Assert.Equal(25, db.Query!.Limit);
            Assert.Equal(expectedCount, pageIds.Count);
            Assert.Equal(expectedCount == 1, cursor is null);
            ids.AddRange(pageIds);
        }
        Assert.Equal(51, ids.Distinct().Count());
        Assert.Equal(3, db.QueryCount);
    }

    [Fact]
    public async Task ResidentDirectoryCursorCannotBeUsedForPendingRegistrations()
    {
        using var db = new PagedDynamo("residents");
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var directory = await store.ListResidentsAsync(null, default);
        await Assert.ThrowsAsync<OsbbRequests.AdminApi.Models.ApiError>(() => store.ListRegistrationsAsync(directory.Cursor, default));
        Assert.Equal(1, db.QueryCount);
    }

    [Fact]
    public async Task MultipleStatusesUseOneQueryCombinedWithOtherFiltersAndCursor()
    {
        using var db = new PagedDynamo("requests");
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var filters = new OsbbRequests.AdminApi.Models.RequestFilters(["New", "InProgress"], "інше", "опис", null, null, "Complex");
        var first = await store.ListRequestsAsync(filters, null, default);
        await store.ListRequestsAsync(filters, first.Cursor, default);
        var query = db.Query!;
        Assert.Equal(2, db.QueryCount);
        Assert.Equal(25, query.Limit);
        Assert.NotEmpty(query.ExclusiveStartKey);
        Assert.Equal("BuildingRequestsIndex", query.IndexName);
        Assert.Equal("requestStatus IN (:status0, :status1) AND requestCategory = :requestCategory AND contains(searchText, :search) AND requestTarget = :target", query.FilterExpression);
        Assert.Equal("New", query.ExpressionAttributeValues[":status0"].S);
        Assert.Equal("InProgress", query.ExpressionAttributeValues[":status1"].S);
        Assert.Equal("complex", query.ExpressionAttributeValues[":building"].S);
    }

    private sealed class PagedDynamo(string list) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public QueryRequest? Query;
        public int QueryCount;
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        {
            Query = request;
            QueryCount++;
            var key = list == "administrators" ? "UserId" : "sk";
            var all = Enumerable.Range(1, 51).Select(Item).ToList();
            var start = request.ExclusiveStartKey?.Count > 0
                ? all.FindIndex(i => i[key].S == request.ExclusiveStartKey[key].S) + 1 : 0;
            var items = all.Skip(start).Take(request.Limit!.Value).ToList();
            var last = start + items.Count < all.Count ? items[^1].Where(p => p.Key is "pk" or "sk" or "requestBuildingId" or "requestCreatedAt" or "UserId" or "Directory" or "Email").ToDictionary() : [];
            return Task.FromResult(new QueryResponse { Items = items, LastEvaluatedKey = last });
        }
        private Dictionary<string, AttributeValue> Item(int id)
        {
            var number = id.ToString("D4", CultureInfo.InvariantCulture);
            if (list == "administrators") return new()
            {
                ["UserId"] = new(number),
                ["Directory"] = new("ADMINS"),
                ["Email"] = new(number + "@example.invalid"),
                ["IsActive"] = new() { BOOL = true },
                ["Version"] = new() { N = "1" }
            };
            var item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new(list == "requests" ? "USER#42" : "BUILDING#complex"),
                ["sk"] = new((list == "requests" ? "REQUEST#" : list == "residents" ? "RESIDENT#" : "REGISTRATION#") + number)
            };
            if (list == "requests")
            {
                item["payload"] = new(JsonSerializer.Serialize(new MaintenanceRequest(id, 42, "complex", "інше", "", "Опис проблеми", DateTimeOffset.UnixEpoch, Scope: RequestScope.Complex)));
                item["requestBuildingId"] = new("complex");
                item["requestCreatedAt"] = new("2026-10-23#" + number);
                item["version"] = new() { N = "1" };
            }
            else item["payload"] = new(JsonSerializer.Serialize(new ResidentRegistration(number, id, "complex",
                list == "residents" ? RegistrationStage.Approved : RegistrationStage.Pending, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                "+380500000000", "Мешканець", number)));
            return item;
        }
    }
}
