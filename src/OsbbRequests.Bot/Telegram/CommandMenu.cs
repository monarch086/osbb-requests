using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsbbRequests.Bot.Telegram;

public sealed record MenuCommand(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("registered_only")] bool RegisteredOnly = false);

public static class CommandMenu
{
    private static readonly MenuCommand[] Commands = Load();
    private static MenuCommand[] Load()
    {
        using var stream = typeof(CommandMenu).Assembly.GetManifestResourceStream("OsbbRequests.Bot.Telegram.command-menu.json")
            ?? throw new InvalidOperationException("Command menu resource missing.");
        return JsonSerializer.Deserialize<MenuCommand[]>(stream) ?? throw new InvalidOperationException("Invalid command menu.");
    }
    public static string? CommandForButton(string text, bool registered)
    {
        var selected = ForResident(registered).FirstOrDefault(command => string.Equals(command.Description, text, StringComparison.Ordinal));
        if (selected is not null) return selected.Command;
        var previous = text switch
        {
            "Початок роботи та перевірка доступу" => "start",
            "Створити заявку для будинку або комплексу" => "new",
            "Переглянути власні заявки" => "my",
            "Відкриті заявки для мешканців комплексу" => "complex",
            "Видалити власну нову заявку" => "delete",
            "Деталі заявки: /request номер" => "request",
            "Довідка: контакти диспетчерів" => "contacts",
            _ => null
        };
        return ForResident(registered).Any(command => command.Command == previous) ? previous : null;
    }
    public static IReadOnlyList<MenuCommand> ForResident(bool registered) =>
        Commands.Where(command => registered ? command.Command != "start" : !command.RegisteredOnly).ToArray();
}
