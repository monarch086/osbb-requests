using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RequestPriorityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T10:00:00Z", CultureInfo.InvariantCulture);
    private static MaintenanceRequest Ticket(int id, string priority = "Normal", RequestStatus status = RequestStatus.New) => new(id, 42,
        "complex", "інше", "", "Опис заявки", Now.AddMinutes(id), status, Priority: priority, Scope: RequestScope.Complex);
    private static Dictionary<string, AttributeValue> Row(MaintenanceRequest value) => new()
    {
        ["pk"] = new("USER#42"),
        ["sk"] = new("REQUEST#" + value.Id.ToString("D20", CultureInfo.InvariantCulture)),
        ["requestBuildingId"] = new(value.BuildingId),
        ["requestPriorityCreatedAt"] = new(RequestPriorityOrder.Key(value)),
        ["payload"] = new(JsonSerializer.Serialize(value)),
        ["version"] = new() { N = "1" }
    };

    [Fact]
    public async Task PaginationSortsWholeDatasetByPriorityThenNewestAcrossPageBoundaries()
    {
        var data = Enumerable.Range(1, 61).Select(id => Ticket(id, new[] { "Normal", "Urgent", "Low", "High" }[id % 4])).ToList();
        using var db = new PriorityDynamo(data);
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var seen = new List<MaintenanceRequest>();
        string? cursor = null;
        do
        {
            db.Queries.Clear();
            var page = await store.ListRequestsAsync(new(null, null, null, null, null), cursor, default);
            Assert.InRange(page.Items.Count, 1, 25);
            Assert.InRange(db.Queries.Count, 1, 4);
            Assert.All(db.Queries, query =>
            {
                Assert.Equal("BuildingPriorityRequestsIndex", query.IndexName);
                Assert.False(query.ScanIndexForward);
            });
            seen.AddRange(page.Items.Select(item => item.Request));
            cursor = page.Cursor;
        } while (cursor is not null);
        Assert.Equal(data.OrderByDescending(item => RequestPriorityOrder.Rank(item.Priority)).ThenByDescending(item => item.CreatedAt).Select(item => item.Id), seen.Select(item => item.Id));
        Assert.Equal(61, seen.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task EmptyFilteredPageKeepsUrgentCursorBeforeAdvancingToLowerPriorities()
    {
        using var db = new PriorityDynamo(Enumerable.Range(1, 40).Select(id => Ticket(id, "Urgent", RequestStatus.Completed))
            .Concat(Enumerable.Range(41, 15).Select(id => Ticket(id, "High"))).ToList());
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var filters = new RequestFilters(["New"], null, null, "2026-10-25", "2026-10-25");
        var first = await store.ListRequestsAsync(filters, null, default);
        Assert.Empty(first.Items);
        Assert.NotNull(first.Cursor);
        var second = await store.ListRequestsAsync(filters, first.Cursor, default);
        Assert.Equal(10, second.Items.Count);
        Assert.All(second.Items, item => Assert.Equal("High", item.Request.Priority));
        var third = await store.ListRequestsAsync(filters, second.Cursor, default);
        Assert.Equal(5, third.Items.Count);
        Assert.Null(third.Cursor);
        Assert.All(db.Queries, query =>
        {
            Assert.Contains("2026-10-25", query.ExpressionAttributeValues[":lower"].S);
            Assert.Contains("2026-10-25", query.ExpressionAttributeValues[":upper"].S);
            Assert.Contains("requestStatus IN", query.FilterExpression);
        });
    }

    [Fact]
    public async Task DirectoryReturnsFullPagesAndFilteredStatisticsWithoutOverlaps()
    {
        var data = Enumerable.Range(1, 90).Select(id => Ticket(id, id <= 40 ? "Urgent" : "High",
            id <= 40 ? RequestStatus.Completed : id % 2 == 0 ? RequestStatus.New : RequestStatus.InProgress)).ToList();
        using var db = new PriorityDynamo(data, countPageSize: 13);
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var filters = new RequestFilters(["New", "InProgress"], null, null, null, null);
        var first = await store.ListRequestsWithStatisticsAsync(filters, null, 1, default);
        var second = await store.ListRequestsWithStatisticsAsync(filters, first.Cursor, 2, default);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(25, second.Items.Count);
        Assert.Equal(new RequestCounts(50, 25, 25), first.Counts);
        Assert.Equal(first.Counts, second.Counts);
        Assert.Equal(1, first.PageNumber);
        Assert.Equal(2, second.PageNumber);
        Assert.Empty(first.Items.Select(item => item.Request.Id).Intersect(second.Items.Select(item => item.Request.Id)));
        Assert.Equal(data.Where(item => item.Status != RequestStatus.Completed).OrderByDescending(item => item.CreatedAt).Select(item => item.Id),
            first.Items.Concat(second.Items).Select(item => item.Request.Id));
        Assert.Contains(db.Queries, query => query.ProjectionExpression == "payload" && query.Limit is null);
    }

    [Theory]
    [InlineData("complex")]
    [InlineData("keys")]
    public async Task DirectoryStatisticsRespectSectionAndStatusFilters(string section)
    {
        var data = Enumerable.Range(1, 40).Select(id => Ticket(id, status: id % 4 == 0 ? RequestStatus.Completed : RequestStatus.New) with
        { Category = id <= 20 ? RequestCategories.KeyOrder : "інше" }).ToList();
        using var db = new PriorityDynamo(data);
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var page = await store.ListRequestsWithStatisticsAsync(new(null, null, null, null, null, Section: section), null, 1, default);
        Assert.Equal(20, page.Counts.Total);
        Assert.Equal(15, page.Counts.NewCount);
        Assert.Equal(0, page.Counts.ProgressCount);
        var filtered = await store.ListRequestsWithStatisticsAsync(new(["Completed"], null, null, null, null, Section: section), null, 1, default);
        Assert.Equal(5, filtered.Counts.Total);
        Assert.Equal(0, filtered.Counts.NewCount);
        Assert.All(filtered.Items, item => Assert.Equal(RequestStatus.Completed, item.Request.Status));
        Assert.All(page.Items, item => Assert.Equal(section == "keys", item.Request.Category == RequestCategories.KeyOrder));
    }

    [Fact]
    public async Task DirectoryUsesSameFiltersForRowsAndStatistics()
    {
        using var db = new PriorityDynamo([Ticket(1)]);
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var filters = new RequestFilters(["New", "InProgress"], "інше", "Тест", "2026-10-25", "2026-10-26", "Building4Б", 5, "complex");
        await store.ListRequestsWithStatisticsAsync(filters, null, 1, default);
        Assert.Contains(db.Queries, query => query.ProjectionExpression == "payload");
        Assert.Contains(db.Queries, query => query.ProjectionExpression is null);
        Assert.All(db.Queries, query =>
        {
            Assert.Equal("інше", query.ExpressionAttributeValues[":requestCategory"].S);
            Assert.Equal("тест", query.ExpressionAttributeValues[":search"].S);
            Assert.Equal("Building4Б", query.ExpressionAttributeValues[":target"].S);
            Assert.Equal("5", query.ExpressionAttributeValues[":floor"].N);
            Assert.Equal("New", query.ExpressionAttributeValues[":status0"].S);
            Assert.Equal("InProgress", query.ExpressionAttributeValues[":status1"].S);
            Assert.Contains("2026-10-25", query.ExpressionAttributeValues[":lower"].S);
            Assert.Contains("2026-10-26", query.ExpressionAttributeValues[":upper"].S);
            Assert.Contains("requestStatus IN", query.FilterExpression);
            Assert.Contains("contains(searchText", query.FilterExpression);
        });
    }

    [Fact]
    public async Task DateModeReturnsStatisticsUsingDateIndex()
    {
        using var db = new DateDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex", prioritizeRequests: false);
        var page = await store.ListRequestsWithStatisticsAsync(new(null, null, null, "2026-10-25", "2026-10-26"), null, 1, default);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(new RequestCounts(3, 1, 1), page.Counts);
        Assert.Equal(2, db.Queries.Count);
        Assert.All(db.Queries, query =>
        {
            Assert.Equal("BuildingRequestsIndex", query.IndexName);
            Assert.Contains("requestCreatedAt BETWEEN", query.KeyConditionExpression);
            Assert.Equal("2026-10-25", query.ExpressionAttributeValues[":from"].S);
        });
    }

    private sealed class DateDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public List<QueryRequest> Queries { get; } = [];
        public override Task<QueryResponse> QueryAsync(QueryRequest query, CancellationToken ct = default)
        {
            Queries.Add(query);
            var data = new[] { Ticket(1), Ticket(2, status: RequestStatus.InProgress), Ticket(3, status: RequestStatus.Completed) };
            return Task.FromResult(new QueryResponse { Items = data.Select(Row).ToList(), LastEvaluatedKey = [] });
        }
    }

    [Fact]
    public async Task CursorCannotBeReusedForAnotherComplexOrPriorityBucket()
    {
        using var db = new PriorityDynamo(Enumerable.Range(1, 30).Select(id => Ticket(id, "Urgent")).ToList());
        var page = await new DynamoAdminStore(db, "admins", "data", "complex").ListRequestsAsync(new(null, null, null, null, null), null, default);
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "other")
            .ListRequestsAsync(new(null, null, null, null, null), page.Cursor, default));
        Assert.Equal(400, error.Status);
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(page.Cursor!)))!;
        json["Priority"] = JsonSerializer.SerializeToElement(2);
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json)));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "complex")
            .ListRequestsAsync(new(null, null, null, null, null), forged, default))).Status);
    }

    [Fact]
    public async Task ChangingPriorityAtomicallyUpdatesIndexKeyAndAudit()
    {
        using var db = new PriorityDynamo([]);
        var original = Ticket(7);
        var next = original with { Priority = "Urgent" };
        await new DynamoAdminStore(db, "admins", "data", "complex").UpdateRequestAsync(new("admin", "admin@example.invalid", "Admin", true, false, 1), new(original, 4), next, default);
        var update = Assert.Single(db.Transaction!.TransactItems, item => item.Update is not null).Update;
        Assert.Equal(RequestPriorityOrder.Key(next), update.ExpressionAttributeValues[":priorityOrder"].S);
        Assert.Contains("requestPriorityCreatedAt = :priorityOrder", update.UpdateExpression);
        Assert.Single(db.Transaction.TransactItems, item => item.Put?.Item.ContainsKey("changes") == true);
    }

    [Fact]
    public async Task DateRangeAppliesWithinEveryPriorityAndEqualDatesUseStableIdOrder()
    {
        var data = new List<MaintenanceRequest>
        {
            Ticket(1, "Urgent") with { CreatedAt = Now.AddDays(-1) },
            Ticket(2, "Urgent") with { CreatedAt = Now },
            Ticket(10, "Urgent") with { CreatedAt = Now },
            Ticket(3, "High") with { CreatedAt = Now.AddDays(1) },
            Ticket(4, "Low") with { CreatedAt = Now }
        };
        using var db = new PriorityDynamo(data);
        var page = await new DynamoAdminStore(db, "admins", "data", "complex")
            .ListRequestsAsync(new(null, null, null, "2026-10-25", "2026-10-25"), null, default);
        Assert.Equal(new long[] { 10, 2, 4 }, page.Items.Select(item => item.Request.Id));
        Assert.Null(page.Cursor);
    }

    private sealed class PriorityDynamo(List<MaintenanceRequest> data, int? countPageSize = null) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public List<QueryRequest> Queries { get; } = [];
        public TransactWriteItemsRequest? Transaction;
        public override Task<QueryResponse> QueryAsync(QueryRequest query, CancellationToken ct = default)
        {
            Queries.Add(query);
            var values = query.ExpressionAttributeValues;
            var candidates = data.Where(item => item.BuildingId == values[":building"].S)
                .Select(Row).Where(item => string.CompareOrdinal(item["requestPriorityCreatedAt"].S, values[":lower"].S) >= 0 &&
                    string.CompareOrdinal(item["requestPriorityCreatedAt"].S, values[":upper"].S) <= 0)
                .OrderByDescending(item => item["requestPriorityCreatedAt"].S, StringComparer.Ordinal).ToList();
            var start = query.ExclusiveStartKey is { Count: > 0 }
                ? candidates.FindIndex(item => item["sk"].S == query.ExclusiveStartKey["sk"].S) + 1 : 0;
            var evaluated = candidates.Skip(start).Take(query.Limit ?? countPageSize ?? int.MaxValue).ToList();
            var statuses = values.Where(pair => pair.Key.StartsWith(":status", StringComparison.Ordinal)).Select(pair => pair.Value.S).ToHashSet();
            var items = evaluated.Where(item =>
            {
                var request = JsonSerializer.Deserialize<MaintenanceRequest>(item["payload"].S)!;
                if (statuses.Count > 0 && !statuses.Contains(request.Status.ToString())) return false;
                if (values.ContainsKey(":keyCategory") &&
                    (request.Category == RequestCategories.KeyOrder) != query.FilterExpression!.StartsWith("requestCategory = :keyCategory", StringComparison.Ordinal)) return false;
                return true;
            }).ToList();
            var last = start + evaluated.Count < candidates.Count
                ? evaluated[^1].Where(pair => pair.Key is "pk" or "sk" or "requestBuildingId" or "requestPriorityCreatedAt").ToDictionary() : [];
            return Task.FromResult(new QueryResponse { Items = items, ScannedCount = evaluated.Count, LastEvaluatedKey = last });
        }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        { Transaction = request; return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
