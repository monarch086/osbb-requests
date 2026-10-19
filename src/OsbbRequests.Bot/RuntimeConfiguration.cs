namespace OsbbRequests.Bot;

internal static class RuntimeConfiguration
{
    public static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Missing configuration: {name}.");
}
