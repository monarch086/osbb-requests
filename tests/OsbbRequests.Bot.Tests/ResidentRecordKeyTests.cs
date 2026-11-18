using OsbbRequests.Shared.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentRecordKeyTests
{
    [Theory]
    [InlineData("BUILDING#complex", "RESIDENT#00000000000000000042", true)]
    [InlineData("REGISTRATION#id", "EVENT#TYPE#event", true)]
    [InlineData("REGISTRATION#id", "EVENT#DECIDED", false)]
    [InlineData("BUILDING#complex", "REGISTRATION#id", false)]
    [InlineData("BUILDING#other", "RESIDENT#00000000000000000042", false)]
    [InlineData("USER#42", "REGISTRATION", false)]
    public void SparseIndexCoversOnlyDirectoryAndEditHistory(string partition, string order, bool included) =>
        Assert.Equal(included, ResidentRecordKey.IsDirectoryOrHistory("complex", partition, order));
}
