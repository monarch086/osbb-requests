using OsbbRequests.Domain.Residents;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("067 123-45-67")]
    [InlineData("380671234567")]
    [InlineData("+380 (67) 123-45-67")]
    public void NormalizesEquivalentPhones(string phone) => Assert.Equal("+380671234567", PhoneNumber.Normalize(phone));

    [Theory]
    [InlineData("")]
    [InlineData("+380abc")]
    [InlineData("0")]
    public void RejectsInvalidPhones(string phone) => Assert.Throws<ArgumentException>(() => PhoneNumber.Normalize(phone));
}
