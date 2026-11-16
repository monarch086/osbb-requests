using OsbbRequests.Shared.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ResidentPhoneIndexTests
{
    [Theory]
    [InlineData("067 123-45-67")]
    [InlineData("380671234567")]
    [InlineData("+380 (67) 123-45-67")]
    public void NormalizesEquivalentPhones(string phone) => Assert.Equal("+380671234567", ResidentPhoneIndex.Normalize(phone));

    [Theory]
    [InlineData("")]
    [InlineData("+380abc")]
    [InlineData("0")]
    public void RejectsInvalidPhones(string phone) => Assert.Throws<ArgumentException>(() => ResidentPhoneIndex.Normalize(phone));

    [Fact]
    public void RejectsInvalidComplexAndResidentKeys()
    {
        Assert.Throws<ArgumentException>(() => ResidentPhoneIndex.LookupKey("other#complex", "+380671234567"));
        Assert.Throws<ArgumentException>(() => ResidentPhoneIndex.ItemKey("", 1, "+380671234567"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ResidentPhoneIndex.ItemKey("building", 0, "+380671234567"));
    }

    [Fact]
    public void KeySupportsSeveralPhonesAndSharedNumbersWithoutCrossComplexCollisions()
    {
        var phone = "+380671234567";
        Assert.Equal(ResidentPhoneIndex.LookupKey("building", phone), ResidentPhoneIndex.LookupKey("building", "0671234567"));
        Assert.NotEqual(ResidentPhoneIndex.LookupKey("building", phone), ResidentPhoneIndex.LookupKey("other", phone));
        Assert.NotEqual(ResidentPhoneIndex.ItemKey("building", 1, phone), ResidentPhoneIndex.ItemKey("building", 2, phone));
        Assert.NotEqual(ResidentPhoneIndex.ItemKey("building", 1, phone), ResidentPhoneIndex.ItemKey("building", 1, "+380931234567"));
    }
}
