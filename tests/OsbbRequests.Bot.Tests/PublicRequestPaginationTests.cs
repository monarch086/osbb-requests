using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Commands;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class PublicRequestPaginationTests
{
    private static MaintenanceRequest Item(int id, RequestStatus status = RequestStatus.New, RequestVisibility visibility = RequestVisibility.Public,
        string category = "Інше") => new(id, 42, "building", category, "", "Опис заявки", DateTimeOffset.UnixEpoch.AddMinutes(id),
            status, visibility, Scope: RequestScope.Complex);

    [Fact]
    public async Task FillsPagesAcrossFilteredAndPrivateRecordsWithoutSkipping()
    {
        var records = Enumerable.Range(1, 12).Select(id => id switch
        {
            11 => Item(id, RequestStatus.Completed),
            10 => Item(id, visibility: RequestVisibility.Private),
            9 => Item(id, category: RequestCategories.KeyOrder),
            8 => Item(id, RequestStatus.Rejected),
            _ => Item(id)
        }).ToArray();
        using var db = new PagingDynamo(records);
        var store = new DynamoCommandRepository(db, "data");
        var first = await store.ListPublicRequestsAsync("building", null, default);
        Assert.Equal(new long[] { 12, 7, 6, 5, 4 }, first.Items.Select(item => item.Id));
        Assert.NotNull(first.NextBefore);
        Assert.True(db.Queries > 1);
        var second = await store.ListPublicRequestsAsync("building", first.NextBefore, default);
        Assert.Equal(new long[] { 3, 2, 1 }, second.Items.Select(item => item.Id));
        Assert.Null(second.NextBefore);
        Assert.All(db.Gets, get => Assert.True(get.ConsistentRead));
    }

    [Fact]
    public async Task LongTelegramMessageContinuesFromTheActualIndexKey()
    {
        var records = Enumerable.Range(1, 5).Select(id => Item(id) with { CreatedAt = DateTimeOffset.UnixEpoch, Description = new string('а', 2000) }).ToArray();
        using var db = new PagingDynamo(records);
        var store = new DynamoCommandRepository(db, "data");
        var outcome = await new ComplexRequestsService(store, "building").ListAsync(42, "", default);
        var ids = System.Text.RegularExpressions.Regex.Matches(outcome.Reply, @"^№(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.NotEmpty(ids);
        Assert.True(ids.Length < 5);
        var last = records.Single(item => item.Id == ids[^1]);
        var actualKey = last.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#" + last.Id.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(actualKey, outcome.Pagination!.Before);
        var next = await store.ListPublicRequestsAsync("building", actualKey, default);
        Assert.Equal(ids[^1] - 1, next.Items[0].Id);
    }

    [Fact]
    public async Task ReadBudgetReturnsCursorForContinuationWithoutSkipping()
    {
        var records = Enumerable.Range(2, 130).Select(id => Item(id, visibility: RequestVisibility.Private)).Append(Item(1)).ToArray();
        using var db = new PagingDynamo(records);
        var store = new DynamoCommandRepository(db, "data");
        var first = await store.ListPublicRequestsAsync("building", null, default);
        Assert.Empty(first.Items);
        Assert.NotNull(first.NextBefore);
        Assert.Equal(25, db.Queries);
        var next = await store.ListPublicRequestsAsync("building", first.NextBefore, default);
        Assert.Equal(1, Assert.Single(next.Items).Id);
        Assert.Null(next.NextBefore);
    }

    private sealed class PagingDynamo(MaintenanceRequest[] records) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public int Queries;
        public List<GetItemRequest> Gets { get; } = [];
        private static string SortKey(MaintenanceRequest item) => item.CreatedAt.ToString("O", CultureInfo.InvariantCulture) + "#" + item.Id.ToString(CultureInfo.InvariantCulture);
        private static Dictionary<string, AttributeValue> Key(MaintenanceRequest item) => new()
        {
            ["pk"] = new("USER#42"),
            ["sk"] = new("REQUEST#" + item.Id.ToString("D20", CultureInfo.InvariantCulture)),
            ["requestBuildingId"] = new("building"),
            ["requestCreatedAt"] = new(SortKey(item))
        };
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        {
            Queries++;
            var before = request.ExclusiveStartKey?.GetValueOrDefault("requestCreatedAt")?.S ?? request.ExpressionAttributeValues.GetValueOrDefault(":before")?.S;
            var candidates = records.Where(item => before is null || string.CompareOrdinal(SortKey(item), before) < 0)
                .OrderByDescending(SortKey, StringComparer.Ordinal).ToArray();
            var evaluated = candidates.Take(request.Limit!.Value).ToArray();
            var items = evaluated.Where(item => item.Status is RequestStatus.New or RequestStatus.InProgress).Select(Key).ToList();
            return Task.FromResult(new QueryResponse { Items = items, LastEvaluatedKey = candidates.Length > evaluated.Length ? Key(evaluated[^1]) : [] });
        }
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            Gets.Add(request);
            var item = records.Single(item => Key(item)["sk"].S == request.Key["sk"].S);
            return Task.FromResult(new GetItemResponse { Item = new() { ["payload"] = new(JsonSerializer.Serialize(item)) } });
        }
    }
}
