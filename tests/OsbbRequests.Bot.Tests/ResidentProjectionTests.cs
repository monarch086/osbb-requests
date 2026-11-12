using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentProjectionTests
{
    [Fact]
    public void ProjectionHasStableUkrainianOrderAndUtcDateAcrossWriters()
    {
        var timestamp = new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero);
        var resident = new ResidentRegistration("id", 99, "building", RegistrationStage.Approved, 5, timestamp, timestamp,
            FirstName: "Іван", LastName: "Ґалаґан");
        var projection = ResidentProjection.Create(resident);
        Assert.Equal("0#20000420000020000F200000200004200000200011#20000B200002200000200011#00000000000000000099", projection.NameOrder);
        Assert.Equal("2026-10-20T11:00:00.0000000+00:00#00000000000000000099", projection.DateOrder);
        Assert.Equal("COMPLEX#building#Approved", projection.Directory);
        Assert.Equal("FamilyMember", projection.ResidentType);
    }

    [Fact]
    public void RelatedRecordKeyPreservesSourceIdentityForHistoryQueries()
    {
        var key = ResidentRecordKey.Create("building", "REGISTRATION#reference", "EVENT#TYPE#timestamp");
        Assert.Equal("COMPLEX#building#RECORD#REGISTRATION#reference#EVENT#TYPE#timestamp", key.Key);
        Assert.Equal("COMPLEX#building#REGISTRATION#reference", key.Group);
        Assert.Equal("EVENT#TYPE#timestamp", key.Order);
    }

    [Theory]
    [InlineData(RegistrationStage.Deleted)]
    [InlineData(RegistrationStage.Rejected)]
    [InlineData(RegistrationStage.Cancelled)]
    [InlineData(RegistrationStage.AwaitingPhone)]
    public void UnlistedStatesDoNotAppearInDirectoryIndexes(RegistrationStage stage)
    {
        var resident = new ResidentRegistration("id", 99, "building", stage, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var projection = ResidentProjection.Create(resident);
        Assert.Null(projection.Directory);
        Assert.Null(projection.NameOrder);
        Assert.Null(projection.DateOrder);
    }
}
