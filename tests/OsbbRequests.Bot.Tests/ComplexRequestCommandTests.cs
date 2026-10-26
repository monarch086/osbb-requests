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
        string complex = "building") => new(id, owner, complex, "електрика", "Біля ліфта", "Не працює освітлення біля ліфта",
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
        Assert.Contains("№7 - Нова", result.Reply);
        Assert.Contains("№8 - В роботі", result.Reply);
        Assert.Contains("Подано: 20.10.2026 13:07\n", result.Reply);
        Assert.Contains("Будинок 4Б", result.Reply);
        Assert.Contains("Весь комплекс", result.Reply);
        Assert.Contains("електрика: Біля ліфта", result.Reply);
        Assert.DoesNotContain("№9", result.Reply);
        Assert.DoesNotContain("№10", result.Reply);
        Assert.DoesNotContain("№11", result.Reply);
        Assert.Contains("Не працює освітлення біля ліфта", result.Reply);
        Assert.DoesNotContain("Приватна відповідь адміністратора", result.Reply);
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
        Assert.Equal(10, token.Length);
        Assert.True(ComplexRequestCursor.IsShortToken(token));
        var result = await Apply(repository, 2, next);
        Assert.Contains("№1", result.Reply);
        Assert.DoesNotContain("Наступна сторінка", result.Reply);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("////")]
    [InlineData("7")]
    [InlineData("p_________________________")]
    [InlineData("p_aaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("p_////////////////////////")]
    [InlineData("p_short")]
    public async Task RejectsMalformedComplexCursorWithoutQuerying(string cursor)
    {
        var repository = new MemoryRepository();
        Assert.Contains("Скористайтеся /complex", (await Apply(repository, 1, "/complex " + cursor)).Reply);
        Assert.Equal(0, repository.Queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(-330)]
    [InlineData(840)]
    [InlineData(-840)]
    public async Task ShortCursorPreservesDatePrecisionOffsetAndLargeIds(int offsetMinutes)
    {
        var repository = new MemoryRepository();
        var date = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes)).AddTicks(1234567);
        for (var i = 0; i < 6; i++) repository.Requests.Add(Request() with { Id = long.MaxValue - i, CreatedAt = date });
        var first = await Apply(repository, 1, "/complex");
        var command = first.Reply.Split("Наступна сторінка: ")[1];
        Assert.Equal(10, command.Split(' ')[1].Length);
        var lastId = long.MaxValue - 4;
        var next = await Apply(repository, 2, command);
        Assert.Equal(date.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "#" + lastId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), repository.LastPublicBefore);
        Assert.Contains($"№{long.MaxValue - 5}", next.Reply);
        Assert.DoesNotContain($"№{lastId}", next.Reply);
    }

    [Theory]
    [InlineData("p_CN8w2rV_9KEAAAAAI0mWAQAA", "2026-10-23T07:53:21.5965345+00:00#00000000000592025089")]
    [InlineData("MjAyNi0xMC0yM1QwNzo1MzoyMS41OTY1MzQ1KzAwOjAwIzU5MjAyNTA4OQ", "2026-10-23T07:53:21.5965345+00:00#592025089")]
    [InlineData("MjAyNi0xMC0yM1QwNzo1MzoyMS41OTY1MzQ1KzAwOjAwIzAwMDAwMDAwMDAwNTkyMDI1MDg5", "2026-10-23T07:53:21.5965345+00:00#00000000000592025089")]
    public async Task PreviouslySentLegacyCursorsRemainValid(string token, string expected)
    {
        var repository = new MemoryRepository();
        await Apply(repository, 1, "/complex " + token);
        Assert.Equal(1, repository.Queries);
        Assert.Equal(expected, repository.LastPublicBefore);
    }

    [Fact]
    public async Task ShortCursorIsPrivateToItsResidentAndComplexAndAcceptsUppercase()
    {
        var repository = new MemoryRepository();
        for (var i = 1; i <= 6; i++) repository.Requests.Add(Request(i));
        var first = await Apply(repository, 1, "/complex");
        var cursor = Assert.Single(repository.ComplexCursors);
        var command = first.Reply.Split("Наступна сторінка: ")[1];
        var handler = Handler(repository);
        repository.Residents.Add(99);
        var foreignUser = await handler.HandleAsync(new(2, 99, 99, command), default);
        Assert.Contains("не знайдено", foreignUser.Reply);
        Assert.Equal(1, repository.Queries);
        var otherComplex = await new ComplexRequestsService(repository, "other").ListAsync(42, cursor.Key.Token, default);
        Assert.Contains("не знайдено", otherComplex.Reply);
        Assert.Equal(1, repository.Queries);
        Assert.Contains("№1", (await Apply(repository, 3, "/complex " + cursor.Key.Token.ToUpperInvariant())).Reply);
        repository.Residents.Clear();
        Assert.Contains("/start", (await Apply(repository, 4, command)).Reply);
        Assert.Equal(2, repository.Queries);
    }

    [Fact]
    public async Task FailedDeliveryRetryReusesTheCommittedShortCursor()
    {
        var repository = new MemoryRepository();
        for (var i = 1; i <= 6; i++) repository.Requests.Add(Request(i));
        var telegram = new FakeTelegram { Fail = true };
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Message(1, "/complex"), default));
        var cursor = Assert.Single(repository.ComplexCursors);
        var receipt = repository.Replies[1];
        telegram.Fail = false;
        await processor.ProcessAsync(Message(1, "/complex"), default);
        Assert.Equal(cursor, Assert.Single(repository.ComplexCursors));
        Assert.Equal(receipt.Text, repository.Replies[1].Text);
        Assert.Contains("№1", (await Apply(repository, 2, receipt.Text.Split("Наступна сторінка: ")[1])).Reply);
    }

    [Fact]
    public async Task FailedCommitDoesNotExposeOrPersistAnUnusableShortCursor()
    {
        var repository = new MemoryRepository { FailCommit = true };
        for (var i = 1; i <= 6; i++) repository.Requests.Add(Request(i));
        var telegram = new FakeTelegram();
        var processor = new CommandProcessor(repository, Handler(repository), telegram);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(Message(1, "/complex"), default));
        Assert.Empty(repository.ComplexCursors);
        Assert.Empty(repository.Replies);
        Assert.Empty(telegram.Messages);
        repository.FailCommit = false;
        await processor.ProcessAsync(Message(1, "/complex"), default);
        Assert.Single(repository.ComplexCursors);
        Assert.Single(telegram.Messages);
    }

    [Fact]
    public async Task EqualDatesAndLongDescriptionsPaginateWithoutSkippingIds()
    {
        var repository = new MemoryRepository();
        foreach (var id in new long[] { 7, 10, 99, 100, 101, 1000 })
            repository.Requests.Add(Request() with { Id = id, Description = new string('О', 2000) });
        var seen = new List<long>();
        var command = "/complex";
        for (var update = 1; update <= 10; update++)
        {
            var page = await Apply(repository, update, command);
            Assert.True(page.Reply.Length <= 4096);
            seen.AddRange(System.Text.RegularExpressions.Regex.Matches(page.Reply, @"^№(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            var parts = page.Reply.Split("Наступна сторінка: ");
            if (parts.Length == 1) break;
            command = parts[1];
        }
        Assert.Equal(new long[] { 1000, 101, 100, 99, 10, 7 }, seen);
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

    [Fact]
    public async Task LongDescriptionsPaginateWithoutSkippingPublicRequestsBetweenPrivateOnes()
    {
        var repository = new MemoryRepository();
        for (var id = 1; id <= 13; id++)
            repository.Requests.Add(Request(id, owner: 999, visibility: id % 3 == 0 ? RequestVisibility.Private : RequestVisibility.Public)
                with
            { Description = ($"Опис{id}:").PadRight(2000, 'О') });
        var seen = new List<long>();
        var command = "/complex";
        for (var update = 1; update <= 15; update++)
        {
            var page = await Apply(repository, update, command);
            Assert.True(page.Reply.Length <= 4096);
            var ids = System.Text.RegularExpressions.Regex.Matches(page.Reply, @"^№(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            foreach (var id in ids)
            {
                Assert.NotEqual(0, id % 3);
                Assert.Contains(repository.Requests.Single(request => request.Id == id).Description, page.Reply);
            }
            seen.AddRange(ids);
            var parts = page.Reply.Split("Наступна сторінка: ");
            if (parts.Length == 1) break;
            command = parts[1];
        }
        Assert.Equal(Enumerable.Range(1, 13).Reverse().Where(id => id % 3 != 0).Select(id => (long)id), seen);
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
        Assert.Contains("/start", result.Reply);
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
