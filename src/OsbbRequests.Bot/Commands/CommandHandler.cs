using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class CommandHandler(ICommandRepository repository, string botUsername, string buildingId,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly RegistrationService registrations = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    public const string Categories = "сантехніка, електрика, прибирання, місця загального користування, інше";
    public const string NewUsage = "Щоб створити заявку, надішліть:\n/new категорія | місце | опис\n\n" +
        "Наприклад:\n/new сантехніка | під’їзд 2, поверх 3 | Протікає труба\n\nКатегорії: " + Categories +
        ".\nМісце: до 200 символів. Опис: від 10 до 2000 символів. Заявка буде приватною.";
    public const string Help = "Команди бота ОСББ:\n/start — початок роботи\n/help — довідка\n" +
        "/register — реєстрація для доступу\n/cancel — скасувати незавершену реєстрацію\n/categories — категорії\n/new — створити приватну заявку\n/my — власні заявки\n" +
        "/request номер — деталі власної заявки\n\n" +
        "Для роботи із заявками голова ОСББ має надати вам доступ. Фото та коментарі ще не підтримуються.";

    public async Task<CommandOutcome> HandleAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = BotCommand.Parse(envelope.Text, botUsername);
        if (command is null)
        {
            if (await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
                return new CommandOutcome("Скористайтеся /help, щоб переглянути команди бота.");
            return await registrations.ContinueAsync(envelope, cancellationToken);
        }

        switch (command.Name)
        {
            case "start":
                return await registrations.StartAsync(envelope.UserId, cancellationToken);
            case "register":
                return await registrations.StartAsync(envelope.UserId, cancellationToken, restart: true);
            case "cancel":
                return await registrations.CancelAsync(envelope.UserId, cancellationToken);
            case "help":
                return new CommandOutcome(Help);
            case "categories":
                return new CommandOutcome("Категорії заявок: " + Categories + ".");
            case not ("new" or "my" or "request"):
                return new CommandOutcome("Невідома команда. Перелік команд: /help.");
        }

        if (!await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
            return await registrations.StatusAsync(envelope.UserId, cancellationToken);

        return command.Name switch
        {
            "new" => CreateRequest(envelope, command.Arguments),
            "my" => await ListAsync(envelope.UserId, command.Arguments, cancellationToken),
            _ => await DetailAsync(envelope.UserId, command.Arguments, cancellationToken)
        };
    }

    private CommandOutcome CreateRequest(CommandEnvelope envelope, string arguments)
    {
        var parts = arguments.Split('|', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return new CommandOutcome(NewUsage);

        var category = parts[0].ToLowerInvariant();
        if (!Categories.Split(", ").Contains(category, StringComparer.Ordinal))
            return new CommandOutcome("Оберіть категорію зі списку: " + Categories + ".\n\n" + NewUsage);
        if (parts[1].Length is < 1 or > 200 || parts[2].Length is < 10 or > 2000)
            return new CommandOutcome(NewUsage);

        var request = new MaintenanceRequest(envelope.UpdateId, envelope.UserId, buildingId,
            category, parts[1], parts[2], clock.GetUtcNow());
        return new CommandOutcome($"Заявку №{request.Id} створено. Статус: Нова. Видимість: приватна.\n" +
            $"Деталі: /request {request.Id}", request);
    }

    private async Task<CommandOutcome> ListAsync(long userId, string arguments, CancellationToken cancellationToken)
    {
        long? beforeId = null;
        if (arguments.Length > 0)
        {
            if (!TryId(arguments, out var parsed))
                return new CommandOutcome("Використайте /my або команду наступної сторінки зі списку заявок.");
            beforeId = parsed;
        }

        var page = await repository.ListRequestsAsync(userId, beforeId, cancellationToken);
        if (page.Items.Count == 0)
            return new CommandOutcome("Заявок на цій сторінці немає. Створити заявку: /new.");

        var lines = page.Items.Select(r => $"№{r.Id} — {StatusLabel(r.Status)}\n{r.Category}: {r.Location}\n/request {r.Id}");
        var reply = "Ваші заявки:\n\n" + string.Join("\n\n", lines);
        if (page.NextBeforeId is { } next)
            reply += $"\n\nНаступна сторінка: /my {next}";
        return new CommandOutcome(reply);
    }

    private async Task<CommandOutcome> DetailAsync(long userId, string arguments, CancellationToken cancellationToken)
    {
        if (!TryId(arguments, out var id))
            return new CommandOutcome("Вкажіть номер заявки: /request номер.");
        var request = await repository.GetRequestAsync(userId, id, cancellationToken);
        if (request is null)
            return new CommandOutcome("Заявку не знайдено серед ваших заявок.");
        var localDate = TimeZoneInfo.ConvertTime(request.CreatedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        var text = $"Заявка №{request.Id}\nСтатус: {StatusLabel(request.Status)}\n" +
            $"Категорія: {request.Category}\nМісце: {request.Location}\n" +
            $"Подано: {localDate:dd.MM.yyyy HH:mm} (Київ)\n\n{request.Description}";
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            text += "\n\nПояснення адміністратора: " + request.Resolution;
        return new CommandOutcome(text);
    }

    private static bool TryId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 0;

    private static string StatusLabel(RequestStatus status) => status switch
    {
        RequestStatus.New => "Нова",
        RequestStatus.InProgress => "В роботі",
        RequestStatus.AwaitingClarification => "Очікує уточнення",
        RequestStatus.Completed => "Виконана",
        RequestStatus.Rejected => "Відхилена",
        _ => throw new InvalidOperationException("Unknown request status.")
    };
}
