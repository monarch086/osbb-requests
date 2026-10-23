using System.Text;
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

public sealed class ResidentDirectoryTests
{
    private static readonly ResidentRegistration Registration = new(new string('a', 32), 99, "building",
        RegistrationStage.Pending, 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        "+380500000000", "Олена", "Тестова");
    private static readonly AdminUser Actor = new("admin", "admin@example.org", "Admin", true, false, 1);
    private static Dictionary<string, AttributeValue> Item(ResidentRegistration registration) => new()
    {
        ["payload"] = new(JsonSerializer.Serialize(registration)),
        ["version"] = new() { N = registration.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };
    private static string Cursor(string building, string sk) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new Dictionary<string, string> { ["pk"] = "BUILDING#" + building, ["sk"] = sk })));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyApprovalAddsResidentToDirectoryInSameGuardedTransaction(bool approved)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration) };
        await new DynamoAdminStore(db, "admins", "data", "building").DecideAsync(Actor, Registration.Id,
            new(approved, approved ? null : "Уточніть дані", 4), default);
        var writes = db.Transaction!.TransactItems;
        Assert.Contains(writes, w => w.ConditionCheck?.TableName == "admins");
        Assert.Contains(writes, w => w.Delete?.Key["sk"].S == "REGISTRATION#" + Registration.Id);
        Assert.Equal(2, writes.Count(w => w.Update?.ConditionExpression.Contains(":previous", StringComparison.Ordinal) == true));
        var directory = writes.SingleOrDefault(w => w.Put?.Item.GetValueOrDefault("sk")?.S.StartsWith("RESIDENT#", StringComparison.Ordinal) == true);
        var pending = writes.Where(w => w.Put?.Item.GetValueOrDefault("pk")?.S == "NOTIFICATIONS#building").ToList();
        if (!approved) { Assert.Null(directory); Assert.Empty(pending); return; }
        var job = Assert.Single(pending).Put;
        var notification = JsonSerializer.Deserialize<ResidentNotification>(job.Item["payload"].S)!;
        Assert.Equal(99, notification.OwnerId);
        Assert.Equal("building", notification.ComplexId);
        Assert.Equal("registration-" + Registration.Id + "-v5", notification.Id);
        Assert.Null(notification.RequestId);
        Assert.Contains("схвалено", notification.Text);
        Assert.Contains("/new", notification.Text);
        Assert.DoesNotContain(Registration.Phone!, notification.Text);
        Assert.DoesNotContain(Registration.LastName!, notification.Text);
        Assert.DoesNotContain(Actor.Email, notification.Text);
        Assert.StartsWith("DUE#", job.Item["sk"].S);
        Assert.Equal("0", job.Item["attempts"].N);
        Assert.Equal("attribute_not_exists(pk)", job.ConditionExpression);
        Assert.False(job.Item.ContainsKey("expiresAt"));
        Assert.False(job.Item.ContainsKey("requestBuildingId"));
        Assert.NotNull(directory);
        Assert.Equal("BUILDING#building", directory.Put.Item["pk"].S);
        Assert.Equal("RESIDENT#00000000000000000099", directory.Put.Item["sk"].S);
        var resident = JsonSerializer.Deserialize<ResidentRegistration>(directory.Put.Item["payload"].S)!;
        Assert.Equal(RegistrationStage.Approved, resident.Stage);
        Assert.Equal(Registration.FirstName, resident.FirstName);
        Assert.Equal(5, resident.Version);
        Assert.False(directory.Put.Item.ContainsKey("expiresAt"));
    }

    [Theory]
    [InlineData(RegistrationStage.Approved, 4, "building", 409)]
    [InlineData(RegistrationStage.Pending, 3, "building", 409)]
    [InlineData(RegistrationStage.Pending, 4, "other", 404)]
    public async Task InvalidApprovalDoesNotQueueNotification(RegistrationStage stage, int version, string complex, int status)
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration with { Stage = stage, Version = version, BuildingId = complex }) };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .DecideAsync(Actor, Registration.Id, new(true, null, 4), default));
        Assert.Equal(status, error.Status);
        Assert.Null(db.Transaction);
    }

    [Fact]
    public async Task ApprovalConflictHasNoSeparateNotificationWrite()
    {
        using var db = new RecordingDynamo { Snapshot = Item(Registration), Conflict = true };
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .DecideAsync(Actor, Registration.Id, new(true, null, 4), default));
        Assert.Equal(409, error.Status);
        Assert.Equal(1, db.TransactionCalls);
        Assert.Single(db.Transaction!.TransactItems, w => w.Put?.Item.GetValueOrDefault("pk")?.S == "NOTIFICATIONS#building");
    }

    [Fact]
    public async Task DirectoryUsesScopedConsistentQueryAndPaginatesWithoutExposingInternalIds()
    {
        using var db = new RecordingDynamo();
        db.Result = new()
        {
            Items = [Item(Registration with { Stage = RegistrationStage.Approved }),
                Item(Registration with { BuildingId = "other", Stage = RegistrationStage.Approved }), Item(Registration)],
            LastEvaluatedKey = new() { ["pk"] = new("BUILDING#building"), ["sk"] = new("RESIDENT#00000000000000000099") }
        };
        var store = new DynamoAdminStore(db, "admins", "data", "building");
        var result = await store.ListResidentsAsync(Cursor("building", "RESIDENT#00000000000000000001"), default);
        Assert.True(db.Query!.ConsistentRead);
        Assert.Equal(25, db.Query.Limit);
        Assert.Equal("BUILDING#building", db.Query.ExpressionAttributeValues[":pk"].S);
        Assert.Equal("RESIDENT#", db.Query.ExpressionAttributeValues[":prefix"].S);
        Assert.Equal("RESIDENT#00000000000000000001", db.Query.ExclusiveStartKey["sk"].S);
        Assert.Single(result.Items);
        Assert.Equal(Registration.Phone, result.Items[0].Phone);
        Assert.NotNull(result.Cursor);
        await store.ListResidentsAsync(result.Cursor, default);
        Assert.Equal("RESIDENT#00000000000000000099", db.Query.ExclusiveStartKey["sk"].S);
    }

    [Theory]
    [InlineData("other", "RESIDENT#1")]
    [InlineData("building", "REGISTRATION#1")]
    public async Task ForeignBuildingAndOtherListCursorsAreRejected(string building, string sk)
    {
        using var db = new RecordingDynamo();
        var error = await Assert.ThrowsAsync<ApiError>(() => new DynamoAdminStore(db, "admins", "data", "building")
            .ListResidentsAsync(Cursor(building, sk), default));
        Assert.Equal(400, error.Status);
        Assert.Null(db.Query);
    }

    [Fact]
    public async Task RequestBuildingAndFloorFiltersRemainQueriesWithinTheComplex()
    {
        using var db = new RecordingDynamo();
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        await store.ListRequestsAsync(new(null, null, null, null, null, "Building4В", 25), null, default);
        Assert.Equal("complex", db.Query!.ExpressionAttributeValues[":building"].S);
        Assert.Equal("Building4В", db.Query.ExpressionAttributeValues[":target"].S);
        Assert.Equal("25", db.Query.ExpressionAttributeValues[":floor"].N);
        Assert.Contains("requestTarget = :target", db.Query.FilterExpression);
        Assert.Contains("requestFloor = :floor", db.Query.FilterExpression);
        await store.ListRequestsAsync(new(null, null, null, null, null, "Unspecified"), null, default);
        Assert.Contains("attribute_not_exists(requestTarget)", db.Query.FilterExpression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Completed")]
    public async Task StatusFilterDoesNotImplicitlyHideCompletedRequests(string? status)
    {
        using var db = new RecordingDynamo();
        db.Result = new()
        {
            Items = [],
            LastEvaluatedKey = new()
            {
                ["pk"] = new("USER#1"),
                ["sk"] = new("REQUEST#1"),
                ["requestBuildingId"] = new("complex"),
                ["requestCreatedAt"] = new("date")
            }
        };
        var store = new DynamoAdminStore(db, "admins", "data", "complex");
        var page = await store.ListRequestsAsync(new(status is null ? [] : [status], "електрика", null, null, null, "Building4Б", 25), null, default);
        Assert.Equal("BuildingRequestsIndex", db.Query!.IndexName);
        Assert.Contains("requestCategory = :requestCategory", db.Query.FilterExpression);
        Assert.DoesNotContain(":completed", db.Query.FilterExpression);
        if (status is not null)
        {
            Assert.Contains("requestStatus IN (:status0)", db.Query.FilterExpression);
            Assert.Equal(status, db.Query.ExpressionAttributeValues[":status0"].S);
        }
        else Assert.DoesNotContain("requestStatus", db.Query.FilterExpression);
        Assert.Empty(page.Items);
        Assert.NotNull(page.Cursor);
        await store.ListRequestsAsync(new(null, null, null, null, null), page.Cursor, default);
        Assert.Equal("REQUEST#1", db.Query.ExclusiveStartKey["sk"].S);
    }
    private sealed class RecordingDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(), RegionEndpoint.EUCentral1)
    {
        public Dictionary<string, AttributeValue>? Snapshot;
        public QueryRequest? Query;
        public QueryResponse Result = new() { Items = [] };
        public TransactWriteItemsRequest? Transaction;
        public int TransactionCalls;
        public bool Conflict;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GetItemResponse { Item = Snapshot });
        public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        { Query = request; return Task.FromResult(Result); }
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        { Transaction = request; TransactionCalls++; if (Conflict) throw new TransactionCanceledException("Conflict"); return Task.FromResult(new TransactWriteItemsResponse()); }
    }
}
