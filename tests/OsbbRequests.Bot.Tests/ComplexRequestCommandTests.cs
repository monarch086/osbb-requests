using OsbbRequests.Bot.Commands;
using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;
using Xunit;

namespace OsbbRequests.Bot.Tests;

public sealed class ComplexRequestCommandTests
{
    private static CommandHandler Handler(MemoryRepository repository) => new(repository, "OsbbBot", "building");
    private static CommandEnvelope Message(long id, string text) => new(id, 42, 42, text);
    private static MaintenanceRequest Request(long id = 7, long owner = 42,
        RequestStatus status = RequestStatus.New, RequestVisibility visibility = RequestVisibility.Public,
        string complex = "building") => new(id, owner, complex, "електрика", "Біля ліфта", "Приватна частина не для списку",
            new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero).AddMinutes(id), status, visibility,
            Resolution: "Приватна відповідь адміністратора", Scope: RequestScope.Building, BuildingNumber: "4Б", Floor: 12);
    private static async Task<CommandOutcome> Apply(MemoryRepository repository, long id, string text)
    {
        var command = Message(id, text);
        var outcome = await Handler(repository).HandleAsync(command, default);
        await repository.CommitAsync(command, outcome, default);
        return outcome;
    }

    [Fact]
    public async Task ListsPublicOpenRequestsForWholeComplexWithKyivDateAndNoPrivateFields()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request(7, 99));
        repository.Requests.Add(Request(8, 100, RequestStatus.InProgress) with { Scope = RequestScope.Complex, BuildingNumber = null, Floor = null });
        repository.Requests.Add(Request(9, visibility: RequestVisibility.Private));
        repository.Requests.Add(Request(10, status: RequestStatus.Completed));
        repository.Requests.Add(Request(11, complex: "other"));
        var result = await Apply(repository, 1, "/COMPLEX@OsbbBot");
        Assert.Contains("№7 — Нова", result.Reply);
        Assert.Contains("№8 — В роботі", result.Reply);
        Assert.Contains("Подано: 20.10.2026 13:07 (Київ)", result.Reply);
        Assert.Contains("Будинок 4Б", result.Reply);
        Assert.Contains("Весь комплекс", result.Reply);
        Assert.Contains("електрика: Біля ліфта", result.Reply);
        Assert.DoesNotContain("№9", result.Reply);
        Assert.DoesNotContain("№10", result.Reply);
        Assert.DoesNotContain("№11", result.Reply);
        Assert.DoesNotContain("Приватна", result.Reply);
        Assert.DoesNotContain("99", result.Reply);
        Assert.DoesNotContain("100", result.Reply);
    }

    [Theory]
    [InlineData(RequestStatus.AwaitingClarification)]
    [InlineData(RequestStatus.Completed)]
    [InlineData(RequestStatus.Rejected)]
    public async Task OtherStatusesAreExcludedFromComplexList(RequestStatus status)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request(status: status));
        Assert.DoesNotContain("№7", (await Apply(repository, 1, "/complex")).Reply);
    }

    [Fact]
    public async Task PaginatesPastEmptyPrivatePageAndDoesNotIncludeOwnerInCursor()
    {
        var repository = new MemoryRepository();
        for (var i = 1; i <= 6; i++) repository.Requests.Add(Request(i, owner: 987654321, visibility: i == 1 ? RequestVisibility.Public : RequestVisibility.Private));
        var page = await Apply(repository, 1, "/complex");
        Assert.Contains("На цій сторінці немає", page.Reply);
        var next = page.Reply.Split("Наступна сторінка: ")[1];
        var token = next.Split(' ')[1];
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/').PadRight((token.Length + 3) / 4 * 4, '=')));
        Assert.DoesNotContain("987654321", decoded);
        var result = await Apply(repository, 2, next);
        Assert.Contains("№1", result.Reply);
        Assert.DoesNotContain("Наступна сторінка", result.Reply);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("////")]
    [InlineData("7")]
    public async Task RejectsMalformedComplexCursorWithoutQuerying(string cursor)
    {
        var repository = new MemoryRepository();
        Assert.Contains("Скористайтеся /complex", (await Apply(repository, 1, "/complex " + cursor)).Reply);
        Assert.Equal(0, repository.Queries);
    }

    [Fact]
    public async Task ComplexPageStaysWithinTelegramLimitForMaximumFieldLengths()
    {
        var repository = new MemoryRepository();
        for (var i = 1; i <= 6; i++) repository.Requests.Add(Request(i) with { Category = new string('К', 200), Location = new string('М', 200), Description = new string('О', 2000) });
        var result = await Apply(repository, 1, "/complex");
        Assert.True(result.Reply.Length <= 4096);
        Assert.Contains("Наступна сторінка", result.Reply);
    }

    [Theory]
    [InlineData("/complex")]
    [InlineData("/delete")]
    [InlineData("/delete 7")]
    public async Task BothCommandsRequireMembership(string text)
    {
        var repository = new MemoryRepository();
        repository.Residents.Clear();
        var result = await Apply(repository, 1, text);
        Assert.Contains("/register", result.Reply);
        Assert.Null(result.Deletion);
        Assert.Null(result.Lookup);
        Assert.Equal(0, repository.Queries);
    }

    [Theory]
    [InlineData("/delete", true)]
    [InlineData("/DELETE@OsbbBot 7", false)]
    public async Task DeletesOwnNewRequestOnlyAfterConfirmation(string command, bool needsNumber)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request(visibility: RequestVisibility.Private));
        var start = await Apply(repository, 1, command);
        if (needsNumber)
        {
            Assert.Contains("Введіть номер", start.Reply);
            await Apply(repository, 2, "7");
        }
        Assert.Single(repository.Requests);
        Assert.Equal(7, repository.Lookups[42].RequestId);
        Assert.Null(start.Deletion);
        var result = await Apply(repository, 3, "Так");
        Assert.Contains("№7 видалено", result.Reply);
        Assert.Empty(repository.Requests);
        Assert.False(repository.Lookups[42].IsActive);
        Assert.DoesNotContain("№7", (await Apply(repository, 4, "/my")).Reply);
        Assert.Contains("не знайдено", (await Apply(repository, 5, "/request 7")).Reply);
    }

    [Theory]
    [InlineData("/cancel")]
    [InlineData("/my")]
    [InlineData("/request 7")]
    [InlineData("/complex")]
    public async Task AnotherCommandCancelsDeletion(string command)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/delete 7");
        await Apply(repository, 2, command);
        await Apply(repository, 3, "Так");
        Assert.Single(repository.Requests);
        Assert.False(repository.Lookups[42].IsActive);
    }

    [Fact]
    public async Task InvalidNumberAndConfirmationDoNotDeleteAnything()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/delete");
        Assert.Contains("Введіть номер", (await Apply(repository, 2, "abc")).Reply);
        await Apply(repository, 3, "7");
        Assert.Contains("підтвердження", (await Apply(repository, 4, "7")).Reply);
        Assert.Single(repository.Requests);
        await Apply(repository, 5, "так");
        Assert.Empty(repository.Requests);
    }

    [Theory]
    [InlineData(RequestStatus.InProgress)]
    [InlineData(RequestStatus.AwaitingClarification)]
    [InlineData(RequestStatus.Completed)]
    [InlineData(RequestStatus.Rejected)]
    public async Task NonNewRequestsCannotBeDeleted(RequestStatus status)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request(status: status));
        var result = await Apply(repository, 1, "/delete 7");
        Assert.Contains("лише власну заявку зі статусом «Нова»", result.Reply);
        Assert.Null(result.Deletion);
        Assert.Single(repository.Requests);
    }

    [Theory]
    [InlineData(99, "building")]
    [InlineData(42, "other")]
    public async Task CannotDeleteOtherOwnersOrComplexes(long owner, string complex)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request(owner: owner, complex: complex));
        Assert.Contains("не знайдено", (await Apply(repository, 1, "/delete 7")).Reply);
        Assert.Single(repository.Requests);
        Assert.Empty(repository.Lookups);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdminChangeAfterPromptPreventsDeletion(bool statusChanged)
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/delete 7");
        repository.RequestVersions[7] = 2;
        if (statusChanged) repository.Requests[0] = repository.Requests[0] with { Status = RequestStatus.InProgress };
        var result = await Apply(repository, 2, "Так");
        Assert.Null(result.Deletion);
        Assert.Single(repository.Requests);
        Assert.False(repository.Lookups[42].IsActive);
    }

    [Fact]
    public async Task RevokedMembershipBlocksConfirmation()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/delete 7");
        repository.Residents.Clear();
        Assert.Null((await Apply(repository, 2, "Так")).Deletion);
        Assert.Single(repository.Requests);
    }

    [Fact]
    public async Task ConcurrentAdminUpdateAfterReadRejectsDeletionAndRetryExplainsChangedStatus()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        await Apply(repository, 1, "/delete 7");
        var command = Message(2, "Так");
        var outcome = await Handler(repository).HandleAsync(command, default);
        Assert.NotNull(outcome.Deletion);
        repository.Requests[0] = repository.Requests[0] with { Status = RequestStatus.InProgress };
        repository.RequestVersions[7] = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CommitAsync(command, outcome, default));
        Assert.False(repository.Replies.ContainsKey(2));
        Assert.True(repository.Lookups[42].IsActive);
        Assert.Contains("лише власну заявку", (await Apply(repository, 2, "Так")).Reply);
        Assert.Single(repository.Requests);
    }

    [Fact]
    public async Task DeletionRetryUsesReceiptAndDoesNotDeleteTwice()
    {
        var repository = new MemoryRepository();
        repository.Requests.Add(Request());
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        await processor.ProcessAsync(Message(1, "/delete 7"), default);
        telegram.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Message(2, "Так"), default));
        Assert.Empty(repository.Requests);
        telegram.Fail = false;
        await processor.ProcessAsync(Message(2, "Так"), default);
        await processor.ProcessAsync(Message(2, "Так"), default);
        await processor.ProcessAsync(Message(1, "/delete 7"), default);
        Assert.Equal(2, repository.CommitCount);
        Assert.Equal(2, telegram.Messages.Count);
        Assert.False(repository.Lookups[42].IsActive);
    }
}
