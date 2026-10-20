using OsbbRequests.Shared.Models;
using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class CommandHandler(ICommandRepository repository, string botUsername, string buildingId,
    TimeProvider? timeProvider = null)
{
    private readonly RegistrationService registrations = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    private readonly RequestCreationService requests = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    public const string Categories = RequestCreationService.Categories;
    public const string NewUsage = RequestCreationService.Usage;
    public const string Help = "Бот ЖК «Зелений острів 2».\n" +
        "/start — початок роботи\n/help — довідка\n/register — реєстрація для доступу до комплексу\n" +
        "/cancel — скасувати незавершену реєстрацію або заявку\n/categories — категорії\n" +
        "/new — створити приватну заявку: будинок або весь комплекс → поверх → категорія → місце → опис\n" +
        "/my — власні заявки\n/request номер — деталі власної заявки\n\n" +
        "Для роботи із заявками голова ОСББ має надати вам доступ. Фото та коментарі ще не підтримуються.";

    public async Task<CommandOutcome> HandleAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = BotCommand.Parse(envelope.Text, botUsername);
        if (command is null)
        {
            if (await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
                return await requests.ContinueAsync(envelope, cancellationToken) ?? new CommandOutcome("Скористайтеся /help, щоб переглянути команди бота ЖК «Зелений острів 2».");
            return await registrations.ContinueAsync(envelope, cancellationToken);
        }

        switch (command.Name)
        {
            case "start":
                return await registrations.StartAsync(envelope.UserId, cancellationToken);
            case "register":
                return await registrations.StartAsync(envelope.UserId, cancellationToken, restart: true);
            case "cancel":
                return await requests.CancelAsync(envelope.UserId, cancellationToken) ?? await registrations.CancelAsync(envelope.UserId, cancellationToken);
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
            "new" => await requests.StartAsync(envelope, command.Arguments, cancellationToken),
            "my" => await ListAsync(envelope.UserId, command.Arguments, cancellationToken),
            _ => await DetailAsync(envelope.UserId, command.Arguments, cancellationToken)
        };
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

        var lines = page.Items.Where(r => r.BuildingId == buildingId).Select(r => $"№{r.Id} — {StatusLabel(r.Status)}\n{ComplexLayout.Label(r.Scope, r.BuildingNumber, r.Floor)}\n{r.Category}: {r.Location}\n/request {r.Id}");
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
        if (request is null || request.BuildingId != buildingId)
            return new CommandOutcome("Заявку не знайдено серед ваших заявок.");
        var localDate = TimeZoneInfo.ConvertTime(request.CreatedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        var text = $"Заявка №{request.Id}\nСтатус: {StatusLabel(request.Status)}\n" +
            $"{ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor)}\nКатегорія: {request.Category}\nМісце: {request.Location}\n" +
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
