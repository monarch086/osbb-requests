using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsbbRequests.Bot.Telegram;

public sealed record MenuCommand(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("description")] string Description);

public static class CommandMenu
{
    private static readonly MenuCommand[] Commands = Load();
    private static MenuCommand[] Load()
    {
        using var stream = typeof(CommandMenu).Assembly.GetManifestResourceStream("OsbbRequests.Bot.Telegram.command-menu.json")
            ?? throw new InvalidOperationException("Command menu resource missing.");
        return JsonSerializer.Deserialize<MenuCommand[]>(stream) ?? throw new InvalidOperationException("Invalid command menu.");
    }
    public static IReadOnlyList<MenuCommand> ForResident(bool registered) =>
        Commands.Where(command => !registered || command.Command != "start").ToArray();
}
