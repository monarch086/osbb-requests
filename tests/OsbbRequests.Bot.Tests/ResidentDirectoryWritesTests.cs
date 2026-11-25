using Amazon.DynamoDBv2.Model;
using OsbbRequests.Domain.Models;
using OsbbRequests.Shared.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentDirectoryWritesTests
{
    private static readonly ResidentRegistration Profile = new("reference", 42, "complex", RegistrationStage.Approved,
        5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Phone: "+380671234567", BuildingNumber: "4Б");

    [Fact]
    public void FirstWriteCountsResidentOnceAndProjectsOnlyListData()
    {
        var writes = new List<TransactWriteItem>();
        ResidentDirectoryWrites.Add(writes, "residents", Profile with { BuildingNumber = null });
        var rows = writes.Where(write => write.Put is not null).Select(write => write.Put).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain("payload", row.Item.Keys);
            Assert.DoesNotContain("phone", row.Item.Keys);
            Assert.Equal("attribute_not_exists(pk)", row.ConditionExpression);
            Assert.Equal("0621355968000000000#reference", row.Item["listOrder"].S);
            Assert.Equal("2534023007999999999#reference", row.Item["listOrderDescending"].S);
        });
        Assert.Contains(rows, x => x.Item["listGroup"].S == "COMPLEX#complex#APPROVED#BUILDING#Unspecified");
        Assert.Equal(2, writes.Count(write => write.Update is not null));
    }

    [Fact]
    public void MoveAndTypeChangeCoalesceDeltasForEachCounter()
    {
        var writes = new List<TransactWriteItem>();
        ResidentDirectoryWrites.Add(writes, "residents",
            Profile with { Version = 6, BuildingNumber = "4Г", ResidentType = ResidentType.Owner }, PreviousRow());
        var counters = writes.Where(write => write.Update is not null).Select(write => write.Update)
            .ToDictionary(update => update.Key["pk"].S.Split("#DIRECTORY_COUNT#")[1]);
        Assert.Equal(3, counters.Count);
        Assert.Equal("-1", counters["ALL"].ExpressionAttributeValues[":delta0"].N);
        Assert.Equal("1", counters["ALL"].ExpressionAttributeValues[":delta1"].N);
        Assert.Equal("-1", counters["BUILDING#4Б"].ExpressionAttributeValues[":delta0"].N);
        Assert.Equal("1", counters["BUILDING#4Г"].ExpressionAttributeValues[":delta0"].N);
        Assert.All(writes.Where(write => write.Put is not null),
            write => Assert.Equal("5", write.Put.ExpressionAttributeValues[":previous"].N));
    }

    [Fact]
    public void DeletionRemovesRowsAndCountsOrGuardsAgainstConcurrentBackfill()
    {
        var deleted = Profile with { Stage = RegistrationStage.Deleted, PersonalDataDeleted = true };
        var writes = new List<TransactWriteItem>();
        ResidentDirectoryWrites.Add(writes, "residents", deleted);
        var check = Assert.Single(writes).ConditionCheck;
        Assert.Equal("attribute_not_exists(pk)", check.ConditionExpression);
        writes.Clear();
        ResidentDirectoryWrites.Add(writes, "residents", deleted, PreviousRow());
        Assert.Equal(2, writes.Count(write => write.Delete is not null));
        Assert.Equal(2, writes.Count(write => write.Update is not null));
        Assert.DoesNotContain(writes, write => write.Put is not null);
    }

    private static Dictionary<string, AttributeValue> PreviousRow() => new()
    {
        ["sourceVersion"] = new() { N = "5" },
        ["residentType"] = new("FamilyMember"),
        ["buildingNumber"] = new("4Б")
    };
}
