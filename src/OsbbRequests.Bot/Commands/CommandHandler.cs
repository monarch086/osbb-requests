using OsbbRequests.Shared.Models;
using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class CommandHandler(ICommandRepository repository, string botUsername, string buildingId,
    TimeProvider? timeProvider = null, ResidentApprovalMode residentApprovalMode = ResidentApprovalMode.Automatic)
{
    private readonly RegistrationService registrations = new(repository, buildingId, timeProvider ?? TimeProvider.System, residentApprovalMode);
    private readonly ResidentProfileService profiles = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    private readonly RequestCreationService requests = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    private readonly RequestLookupService lookup = new(repository, buildingId);
    private readonly ComplexRequestsService complexRequests = new(repository, buildingId);
    public const string Categories = RequestCreationService.Categories;
    public const string NewUsage = RequestCreationService.Usage;
    public const string Help = "Бот ЖК «Зелений острів 2».\n" +
        "/start — початок роботи\n/help — довідка\n/register — реєстрація для доступу до комплексу\n" +
        "/edit_profile — змінити інформацію про себе\n/delete_profile — видалити інформацію про себе\n" +
        "/cancel — скасувати поточний діалог\n/categories — категорії\n" +
        "/new — створити заявку для всіх мешканців комплексу: будинок або весь комплекс → поверх → категорія → опис\n" +
        "/my — власні заявки\n/complex — відкриті заявки для мешканців комплексу\n" +
        "/request номер — деталі власної заявки\n/delete номер — видалити власну нову заявку з підтвердженням\n" +
        "Номер можна ввести одразу або наступним повідомленням після /request або /delete.\n\n" +
        "Для роботи із заявками завершіть реєстрацію. Фото та коментарі ще не підтримуються.";

    public async Task<CommandOutcome> HandleAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var outcome = await HandleCoreAsync(envelope, cancellationToken);
        var command = BotCommand.Parse(envelope.Text, botUsername);
        if (command is not null && command.Name is not ("edit_profile" or "delete_profile" or "cancel") && outcome.ProfileDialog is null)
            outcome = await profiles.ClearAsync(envelope.UserId, outcome, cancellationToken);
        if (command is not null && outcome.Lookup is null)
            outcome = await lookup.ClearAsync(envelope.UserId, outcome, cancellationToken);
        return outcome;
    }

    private async Task<CommandOutcome> HandleCoreAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var command = BotCommand.Parse(envelope.Text, botUsername);
        if (command is null)
        {
            if (await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
                return await profiles.ContinueAsync(envelope, cancellationToken) ?? await lookup.ContinueAsync(envelope, cancellationToken) ?? await requests.ContinueAsync(envelope, cancellationToken) ?? new CommandOutcome("Скористайтеся /help, щоб переглянути команди бота ЖК «Зелений острів 2».");
            return await registrations.ContinueAsync(envelope, cancellationToken);
        }

        switch (command.Name)
        {
            case "start":
                return await registrations.StartAsync(envelope.UserId, cancellationToken);
            case "register":
                return await registrations.StartAsync(envelope.UserId, cancellationToken, restart: true);
            case "cancel":
                return await profiles.CancelAsync(envelope.UserId, cancellationToken) ?? await lookup.CancelAsync(envelope.UserId, cancellationToken) ?? await requests.CancelAsync(envelope.UserId, cancellationToken) ?? await registrations.CancelAsync(envelope.UserId, cancellationToken);
            case "help":
                return new CommandOutcome(Help);
            case "categories":
                return new CommandOutcome("Категорії заявок: " + Categories + ".");
            case not ("new" or "my" or "request" or "complex" or "delete" or "edit_profile" or "delete_profile"):
                return new CommandOutcome("Невідома команда. Перелік команд: /help.");
        }

        if (!await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
            return await registrations.StatusAsync(envelope.UserId, cancellationToken);

        return command.Name switch
        {
            "edit_profile" => await profiles.StartAsync(envelope.UserId, ProfileAction.Edit, cancellationToken),
            "delete_profile" => await profiles.StartAsync(envelope.UserId, ProfileAction.Delete, cancellationToken),
            "new" => await requests.StartAsync(envelope, command.Arguments, cancellationToken),
            "complex" => await complexRequests.ListAsync(command.Arguments, cancellationToken),
            "delete" => await lookup.StartAsync(envelope.UserId, command.Arguments, cancellationToken, RequestLookupAction.Delete),
            "my" => await ListAsync(envelope.UserId, command.Arguments, cancellationToken),
            _ => await lookup.StartAsync(envelope.UserId, command.Arguments, cancellationToken)
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

        var requests = page.Items.Where(r => r.BuildingId == buildingId && r.OwnerId == userId).ToList();
        var lines = requests.Select(r => $"№{r.Id} — {StatusLabel(r.Status)}\n{ComplexLayout.Label(r.Scope, r.BuildingNumber, r.Floor, r.Entrance)}\n" +
            RequestListText.Limit(r.Category, 80) + (string.IsNullOrEmpty(r.Location) ? "" : ": " + RequestListText.Limit(r.Location, 200)) +
            "\n" + RequestListText.Limit(r.Description, 2000) + $"\n/request {r.Id}").ToList();
        string? NextCommand(int count)
        {
            var next = count > 0 && count < requests.Count ? requests[count - 1].Id : page.NextBeforeId;
            return next is null ? null : "/my " + next.Value.ToString(CultureInfo.InvariantCulture);
        }
        var reply = RequestListText.Build("Ваші заявки:", lines, NextCommand);
        return new CommandOutcome(reply);
    }

    private static bool TryId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 0;

    internal static string StatusLabel(RequestStatus status) => status switch
    {
        RequestStatus.New => "Нова",
        RequestStatus.InProgress => "В роботі",
        RequestStatus.AwaitingClarification => "Очікує уточнення",
        RequestStatus.Completed => "Виконана",
        RequestStatus.Rejected => "Відхилена",
        _ => throw new InvalidOperationException("Unknown request status.")
    };
}
