using OsbbRequests.Services;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using OsbbRequests.Aws;
using OsbbRequests.Domain.Admin;
using OsbbRequests.Domain.Models;
using OsbbRequests.Shared.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class OwnerApartmentStoreTests
{
    private static readonly AdminUser Reviewer = new("admin", "admin@example.com", "Admin", true, false, 3);
    private static readonly ResidentRegistration Owner = new("ref", 42, "complex", RegistrationStage.Approved, 6,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ResidentType: ResidentType.Owner);
    private static readonly OwnerApartment Apartment = new("id", "4Г", 13, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(true, ApartmentReviewStatus.Approved)]
    [InlineData(false, ApartmentReviewStatus.Rejected)]
    public async Task DecisionChecksBothVersionsAndCommitsReviewWithNotification(bool approved, ApartmentReviewStatus status)
    {
        using var db = new ApartmentDynamo(Apartment);
        var store = new DynamoAdminStore(db, "admins", "commands", "complex", residentsTable: "residents");

        await store.DecideApartmentAsync(Reviewer, Owner.Id, Apartment.Id, approved, default);

        var writes = db.Transaction!.TransactItems;
        Assert.Equal(4, writes.Count);
        Assert.Equal("3", Assert.Single(writes, write => write.ConditionCheck?.TableName == "admins").ConditionCheck.ExpressionAttributeValues[":version"].N);
        Assert.Equal("6", Assert.Single(writes, write => write.ConditionCheck?.TableName == "residents").ConditionCheck.ExpressionAttributeValues[":version"].N);
        var save = Assert.Single(writes, write => write.Put?.TableName == "residents").Put;
        Assert.Equal("2", save.ExpressionAttributeValues[":version"].N);
        Assert.False(save.Item.ContainsKey(ResidentRecordKey.IndexGroupAttribute));
        var result = Assert.Single(OwnerApartments.Read(save.Item, "complex", Owner.UserId)!.Items);
        Assert.Equal(status, result.Status);
        Assert.Equal(Reviewer.Email, result.ReviewedBy);
        Assert.NotNull(result.ReviewedAt);
        var notification = Assert.Single(writes, write => write.Put?.TableName == "commands").Put;
        Assert.Equal(Owner.Id, JsonSerializer.Deserialize<ResidentNotification>(notification.Item["payload"].S)!.RegistrationId);
    }

    [Fact]
    public async Task PendingCountSumsApartmentsAcrossPagesAndSkipsStaleIndexEntries()
    {
        using var db = new CountDynamo();
        var store = new DynamoAdminStore(db, "admins", "commands", "complex", residentsTable: "residents");
        Assert.Equal(2, await store.CountApartmentReviewsAsync(default));
        Assert.Equal(2, db.Queries);
    }

    private sealed class CountDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public int Queries { get; private set; }

        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        {
            Queries++;
            var first = request.ExclusiveStartKey is null;
            var owner = first ? 42 : 43;
            var key = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new(OwnerApartments.Key("complex", owner)),
                ["recordIndexGroup"] = new(OwnerApartments.ReviewGroup("complex")),
                ["recordOrder"] = new(owner.ToString("D20", System.Globalization.CultureInfo.InvariantCulture))
            };
            return Task.FromResult(new QueryResponse { Items = [key], LastEvaluatedKey = first ? key : [] });
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            var key = request.Key["pk"].S;
            if (key == OwnerApartments.Key("complex", 43)) return Task.FromResult(new GetItemResponse { Item = [] });
            var record = new OwnerApartments("complex", 42, Owner.Id, 1, [
                Apartment, Apartment with { Id = "second" }, Apartment with { Id = "reviewed", Status = ApartmentReviewStatus.Approved }
            ]);
            var item = key == OwnerApartments.Key("complex", 42)
                ? record.Save("residents", null).Put.Item
                : new Dictionary<string, AttributeValue>
                {
                    ["recordData"] = new() { M = new() { ["payload"] = new(JsonSerializer.Serialize(Owner)) } }
                };
            return Task.FromResult(new GetItemResponse { Item = item });
        }
    }

    [Fact]
    public async Task AlreadyReviewedApartmentCannotSendAnotherNotification()
    {
        using var db = new ApartmentDynamo(Apartment with { Status = ApartmentReviewStatus.Approved });
        var store = new DynamoAdminStore(db, "admins", "commands", "complex", residentsTable: "residents");
        var error = await Assert.ThrowsAsync<AdminOperationException>(() => store.DecideApartmentAsync(Reviewer, Owner.Id, Apartment.Id, false, default));
        Assert.Equal(AdminFailure.Conflict, error.Failure);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task ExportReadsOnlyMatchingOwnersAndOnlyReturnsApprovedApartments()
    {
        using var db = new ApartmentDynamo(Apartment);
        var store = new DynamoAdminStore(db, "admins", "commands", "complex", residentsTable: "residents");
        var result = await store.ListApprovedApartmentsForExportAsync([
            Owner, Owner with { Id = "older", UserId = 43 }, Owner with { Id = "missing", UserId = 44 },
            Owner with { UserId = 45, ResidentType = ResidentType.Tenant },
            Owner with { UserId = 46, BuildingId = "other" },
            Owner with { UserId = 47, PersonalDataDeleted = true },
            Owner with { UserId = 48, Stage = RegistrationStage.Pending }
        ], default);
        Assert.Equal("approved", Assert.Single(result[Owner.Id]).Id);
        Assert.Single(result);
        var request = Assert.Single(db.Batch!.RequestItems).Value;
        Assert.True(request.ConsistentRead);
        Assert.Equal(new long[] { 42, 43, 44 }.Select(id => OwnerApartments.Key("complex", id)),
            request.Keys.Select(key => key["pk"].S));
    }

    private sealed class ApartmentDynamo(OwnerApartment apartment) : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public TransactWriteItemsRequest? Transaction { get; private set; }
        public BatchGetItemRequest? Batch { get; private set; }

        public override Task<BatchGetItemResponse> BatchGetItemAsync(BatchGetItemRequest request, CancellationToken ct = default)
        {
            Batch = request;
            var record = new OwnerApartments("complex", Owner.UserId, Owner.Id, 2,
                [apartment, apartment with { Id = "approved", Status = ApartmentReviewStatus.Approved },
                    apartment with { Id = "rejected", Status = ApartmentReviewStatus.Rejected }]);
            var mismatched = record with { OwnerId = 43 };
            return Task.FromResult(new BatchGetItemResponse
            {
                Responses = new() { ["residents"] = [record.Save("residents", null).Put.Item, mismatched.Save("residents", null).Put.Item] }
            });
        }

        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
        {
            Assert.True(request.ConsistentRead);
            var item = request.Key["pk"].S == OwnerApartments.Key("complex", Owner.UserId)
                ? new OwnerApartments("complex", Owner.UserId, Owner.Id, 2, [apartment]).Save("residents", null).Put.Item
                : new Dictionary<string, AttributeValue>
                {
                    ["recordData"] = new() { M = new() { ["payload"] = new(JsonSerializer.Serialize(Owner)) } }
                };
            return Task.FromResult(new GetItemResponse { Item = item });
        }

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
        {
            Transaction = request;
            return Task.FromResult(new TransactWriteItemsResponse());
        }
    }
}
