using System.Globalization;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Data;
using OsbbRequests.AdminApi.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class CompactResidentDirectoryTests
{
    [Fact]
    public async Task NextPageReadsOnlyItsIndexRangeAndUsesCounter()
    {
        using var db = new DirectoryDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex", residentsTable: "residents", compactResidentDirectory: true);
        var first = (await store.ListCompactResidentsAsync(1, "4Б", null, "asc", null, default))!;
        var next = (await store.ListCompactResidentsAsync(2, "4Б", null, "asc", first.NextCursor, default))!;
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(25, next.Items.Count);
        Assert.Empty(first.Items.Select(r => r.Id).Intersect(next.Items.Select(r => r.Id)));
        Assert.Equal(160, next.Total);
        Assert.Equal(2, db.Queries.Count);
        Assert.All(db.Queries, query =>
        {
            Assert.Equal("ResidentDirectoryByDate", query.IndexName);
            Assert.Equal(25, query.Limit);
            Assert.Equal("COMPLEX#complex#APPROVED#BUILDING#4Б", query.ExpressionAttributeValues[":group"].S);
        });
        Assert.Equal(first.NextCursor, next.StartCursor);
        await Assert.ThrowsAsync<ApiError>(() => store.ListCompactResidentsAsync(2, "4В", null, "asc", first.NextCursor, default));
    }

    [Fact]
    public async Task JumpWithTypeFilterResumesAfterLastMatchingRowWithinBatch()
    {
        using var db = new DirectoryDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex", residentsTable: "residents", compactResidentDirectory: true);
        var jumped = (await store.ListCompactResidentsAsync(2, null, "Owner", "desc", null, default))!;
        var fromCursor = (await store.ListCompactResidentsAsync(3, null, "Owner", "desc", jumped.NextCursor, default))!;
        Assert.Equal(80, jumped.Total);
        Assert.Equal(160, jumped.Counts.Total);
        Assert.Equal(51.ToString("x32"), jumped.Items[0].Id);
        Assert.Equal(101.ToString("x32"), fromCursor.Items[0].Id);
        Assert.Equal(25, fromCursor.Items.Count);
        Assert.All(db.Queries, q => Assert.Equal("ResidentDirectoryByDateDescending", q.IndexName));
    }

    [Fact]
    public async Task NamePageUsesBinaryCursorAndRejectsDateCursor()
    {
        using var db = new DirectoryDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex", residentsTable: "residents",
            compactResidentDirectory: true, compactResidentNameDirectory: true);
        var first = (await store.ListCompactResidentsAsync(1, null, null, "desc", null, default, byName: true))!;
        var second = (await store.ListCompactResidentsAsync(2, null, null, "desc", first.NextCursor, default, byName: true))!;
        Assert.Equal(25, second.Items.Count);
        Assert.All(db.Queries, query => Assert.Equal("ResidentDirectoryByNameDescending", query.IndexName));
        Assert.NotNull(db.Queries[1].ExclusiveStartKey["listNameOrderDescending"].B);
        await Assert.ThrowsAsync<ApiError>(() => store.ListCompactResidentsAsync(2, null, null, "desc", first.NextCursor, default));
    }

    private sealed class DirectoryDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public List<QueryRequest> Queries { get; } = [];

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
        {
            Assert.True(request.ConsistentRead);
            Assert.Contains("#DIRECTORY_COUNT#", request.Key["pk"].S);
            return Task.FromResult(new GetItemResponse { Item = new() { ["Owner"] = new() { N = "80" }, ["Tenant"] = new() { N = "80" } } });
        }

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            Queries.Add(request);
            var start = int.Parse(request.ExclusiveStartKey?.GetValueOrDefault("pk")?.S.Split('#')[^1] ?? "0", CultureInfo.InvariantCulture);
            var group = request.ExpressionAttributeValues[":group"];
            var kind = group.S.Contains("#BUILDING#", StringComparison.Ordinal) ? "BUILDING" : "ALL";
            var byName = request.IndexName.Contains("ByName", StringComparison.Ordinal);
            var order = byName ? "listNameOrder" : "listOrder";
            if (request.IndexName.EndsWith("Descending", StringComparison.Ordinal)) order += "Descending";
            Dictionary<string, AttributeValue> Key(int i) => new()
            {
                ["pk"] = new($"COMPLEX#complex#DIRECTORY_ROW#{kind}#{i}"),
                ["listGroup"] = group,
                [order] = byName ? new() { B = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"{i:D19}#{i:x32}")) } : new($"{i:D19}#{i:x32}")
            };
            var end = Math.Min(160, start + request.Limit!.Value);
            var items = Enumerable.Range(start + 1, end - start)
                .Where(i => request.FilterExpression is null || i % 2 != 0)
                .Select(i => new Dictionary<string, AttributeValue>(Key(i))
                {
                    ["registrationId"] = new(i.ToString("x32")),
                    ["residentType"] = new(i % 2 != 0 ? "Owner" : "Tenant")
                }).ToList();
            return Task.FromResult(new QueryResponse { Items = items, Count = items.Count, LastEvaluatedKey = end == 160 ? null : Key(end) });
        }
    }
}
