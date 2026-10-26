namespace OsbbRequests.Bot.Models;

public sealed record ComplexRequestCursor(long UserId, string ComplexId, string Token, string Before)
{
    public const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    public static bool IsShortToken(string value) => value.Length == 10 && value.All(Alphabet.Contains);
}
