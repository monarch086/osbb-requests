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

    private sealed class PriorityDynamo(List<MaintenanceRequest> data) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
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
            var evaluated = candidates.Skip(start).Take(query.Limit!.Value).ToList();
            var items = evaluated.Where(item => !values.ContainsKey(":status0") ||
                JsonSerializer.Deserialize<MaintenanceRequest>(item["payload"].S)!.Status.ToString() == values[":status0"].S).ToList();
            var last = start + evaluated.Count < candidates.Count
                ? evaluated[^1].Where(pair => pair.Key is "pk" or "sk" or "requestBuildingId" or "requestPriorityCreatedAt").ToDictionary() : [];
            return Task.FromResult(new QueryResponse { Items = items, ScannedCount = evaluated.Count, LastEvaluatedKey = last });
        }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        { Transaction = request; return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
