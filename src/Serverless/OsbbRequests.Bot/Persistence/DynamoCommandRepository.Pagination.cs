using System.Globalization;
using Amazon.DynamoDBv2.Model;
using OsbbRequests.Bot.Models;

namespace OsbbRequests.Bot.Persistence;

public sealed partial class DynamoCommandRepository
{
    private static string ComplexCursorKey(string complexId, string token) => "COMPLEX_CURSOR#" + complexId + "#" + token;

    public async Task<string?> GetComplexCursorAsync(long userId, string complexId, string token, CancellationToken cancellationToken)
    {
        if (!ComplexRequestCursor.IsShortToken(token)) return null;
        var item = await GetAsync("USER#" + Number(userId), ComplexCursorKey(complexId, token), cancellationToken);
        if (!long.TryParse(item.GetValueOrDefault("expiresAt")?.N, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt) ||
            expiresAt <= clock.GetUtcNow().ToUnixTimeSeconds()) return null;
        return item.GetValueOrDefault("complexId")?.S == complexId && item.GetValueOrDefault("userId")?.N == Number(userId)
            ? item.GetValueOrDefault("before")?.S : null;
    }

    private TransactWriteItem CursorWrite(CommandEnvelope command, ComplexRequestCursor cursor)
    {
        if (cursor.UserId != command.UserId || string.IsNullOrWhiteSpace(cursor.ComplexId) ||
            !ComplexRequestCursor.IsShortToken(cursor.Token) || string.IsNullOrWhiteSpace(cursor.Before))
            throw new InvalidOperationException("Invalid pagination cursor.");
        var item = Key("USER#" + Number(cursor.UserId), ComplexCursorKey(cursor.ComplexId, cursor.Token));
        item["complexId"] = new(cursor.ComplexId);
        item["userId"] = new() { N = Number(cursor.UserId) };
        item["before"] = new(cursor.Before);
        item["expiresAt"] = new() { N = Number(clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds()) };
        return Put(item);
    }
}
