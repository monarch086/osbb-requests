using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class RequestListFormattingTests
{
    private const string Description = "з 1 листопада потрібно прибрати на усіх стендах інфо про КЖС і розмістити корисну інфо про осбб";
    private static readonly MaintenanceRequest Request = new(592025075, 42, "building", "місця загального користування", "", Description,
        new DateTimeOffset(2026, 10, 23, 7, 47, 0, TimeSpan.Zero), RequestStatus.InProgress, RequestVisibility.Public,
        Scope: RequestScope.Building, BuildingNumber: "4Д");

    [Theory]
    [InlineData("/my")]
    [InlineData("/complex")]
    public async Task ListsSeparateDescriptionFromMetadataAsInTheResidentExample(string command)
    {
        var reply = await List(command, Request);
        Assert.Contains("№592025075 - В роботі\nПодано: 23.10.2026 10:47\nБудинок 4Д\n" +
            "місця загального користування\n~~~~~\n" + Description + "\n~~~~~", reply);
        if (command == "/my") Assert.Contains("~~~~~\n/request 592025075", reply);
    }

    [Theory]
    [InlineData(null, null, null, null, "")]
    [InlineData(RequestScope.Building, "4Д", null, null, "Будинок 4Д\n")]
    [InlineData(RequestScope.Building, "4В", null, 2, "Будинок 4В · під’їзд 2\n")]
    [InlineData(RequestScope.Building, "4Г", 12, null, "Будинок 4Г · поверх 12\n")]
    [InlineData(RequestScope.Building, "4В", 12, 1, "Будинок 4В · під’їзд 1 · поверх 12\n")]
    [InlineData(RequestScope.Building, "4Д", 0, null, "Будинок 4Д · Парадне\n")]
    [InlineData(RequestScope.Complex, null, null, null, "Весь комплекс\n")]
    [InlineData(null, "4Д", null, null, "Будинок 4Д\n")]
    public async Task BothListsShowOnlyKnownLocationParts(RequestScope? scope, string? building, int? floor, int? entrance, string location)
    {
        foreach (var command in new[] { "/my", "/complex" })
        {
            var reply = await List(command, Request with { Scope = scope, BuildingNumber = building, Floor = floor, Entrance = entrance });
            Assert.Contains("Подано: 23.10.2026 10:47\n" + location + "місця загального користування\n~~~~~", reply);
            Assert.DoesNotContain("Не зазначено", reply);
            Assert.DoesNotContain("без прив’язки", reply);
            Assert.DoesNotContain("не уточнено", reply);
        }
    }

    [Theory]
    [InlineData("/my")]
    [InlineData("/complex")]
    public async Task MultilineDescriptionAndHistoricalLocationRemainIntact(string command)
    {
        const string description = "Перший рядок\nДругий рядок 🛠";
        var reply = await List(command, Request with { Description = description, Location = "Біля ліфта" });
        Assert.Contains("місця загального користування: Біля ліфта\n~~~~~\n" + description + "\n~~~~~", reply);
    }

    private static async Task<string> List(string command, MaintenanceRequest request)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(request);
        return (await new CommandHandler(repository, "OsbbBot", "building")
            .HandleAsync(new CommandEnvelope(100, 42, 42, command), default)).Reply;
    }
}
