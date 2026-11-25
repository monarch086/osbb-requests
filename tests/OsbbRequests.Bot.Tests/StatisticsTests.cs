using OsbbRequests.Aws;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.AdminApi.Statistics;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class StatisticsTests
{
    [Fact]
    public async Task RecordingUsesAtomicAddAndKeepsRequestDataOutOfTheKey()
    {
        using var db = new StatisticsDynamo();
        var store = new DynamoStatisticsStore(db, "statistics");
        await store.RecordAsync("GET /admin/requests", 2.5, 1, 300, default);
        var update = Assert.IsType<UpdateItemRequest>(db.Update);
        Assert.Equal("statistics", update.TableName);
        Assert.Equal("API", update.Key["pk"].S);
        Assert.EndsWith("#GET /admin/requests", update.Key["sk"].S, StringComparison.Ordinal);
        Assert.StartsWith("ADD ", update.UpdateExpression, StringComparison.Ordinal);
        Assert.Equal("2.5", update.ExpressionAttributeValues[":read"].N);
        Assert.Equal("300", update.ExpressionAttributeValues[":duration"].N);
    }

    [Fact]
    public void MeasurementsAccumulateReadAndWriteUnitsAcrossOperations()
    {
        var measurement = new RequestMeasurement();
        measurement.Add(new ConsumedCapacity { ReadCapacityUnits = 1.5, WriteCapacityUnits = 2 });
        measurement.Add(new ConsumedCapacity { ReadCapacityUnits = 3, WriteCapacityUnits = 0.5 });
        var result = measurement.Finish();
        Assert.Equal(4.5, result.Read);
        Assert.Equal(2.5, result.Write);
    }

    private sealed class StatisticsDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public UpdateItemRequest? Update;
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
        { Update = request; return Task.FromResult(new UpdateItemResponse()); }
    }
}
