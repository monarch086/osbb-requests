namespace OsbbRequests.CommandProcessor.Models;

public static class ComplexRequestCursor
{
    public const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    public static bool IsShortToken(string value) => value.Length == 10 && value.All(Alphabet.Contains);
}
