using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class PersonalRequestPaginationTests
{
    [Theory]
    [InlineData(10, null)]
    [InlineData(11, 2)]
    [InlineData(9, null)]
    public async Task OnlyAnAdditionalRecordCreatesNextCursor(int count, int? expectedCursor)
    {
        using var db = new PersonalDynamo(count);
        var page = await new DynamoCommandRepository(db, "data").ListRequestsAsync(42, null, default);
        Assert.Equal(Math.Min(count, 10), page.Items.Count);
        Assert.Equal(expectedCursor, page.NextBeforeId);
        Assert.Equal(11, db.Query!.Limit);
        Assert.True(db.Query.ConsistentRead);
    }

    private sealed class PersonalDynamo(int count) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public QueryRequest? Query;
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        {
            Query = request;
            var items = Enumerable.Range(1, count).Reverse().Select(id => new Dictionary<string, AttributeValue>
            {
                ["payload"] = new(JsonSerializer.Serialize(new MaintenanceRequest(id, 42, "complex", "Інше", "", "Опис заявки",
                    DateTimeOffset.UnixEpoch, Scope: RequestScope.Complex)))
            }).ToList();
            return Task.FromResult(new QueryResponse { Items = items, LastEvaluatedKey = [] });
        }
    }
}
