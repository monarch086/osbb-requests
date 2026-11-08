using OsbbRequests.Shared.Models;
using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Bot.Telegram;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class CommandHandler(ICommandRepository repository, string botUsername, string buildingId,
    TimeProvider? timeProvider = null, ResidentApprovalMode residentApprovalMode = ResidentApprovalMode.Automatic)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly RegistrationService registrations = new(repository, buildingId, timeProvider ?? TimeProvider.System, residentApprovalMode);
    private readonly ResidentProfileService profiles = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    private readonly RequestCreationService requests = new(repository, buildingId, timeProvider ?? TimeProvider.System);
    private readonly RequestLookupService lookup = new(repository, buildingId);
    private readonly ComplexRequestsService complexRequests = new(repository, buildingId);
    public const string Categories = RequestCreationService.Categories;
    public const string NewUsage = RequestCreationService.Usage;
    public const string DispatcherContacts = "Телефони диспетчерів:\n\nСантехнік, електрик:\n0632479719\n0936631010\n\n" +
        "Ліфтер:\n0683558674\n0634835206\n0443602845";
    public const string Help = "Бот ЖК «Зелений острів 2».\n" +
        "/start - початок роботи та реєстрація\n/help - довідка\n/contacts - контакти диспетчерів\n" +
        "/account - мій особовий рахунок\n/edit_profile - змінити інформацію про себе\n/delete_profile - видалити інформацію про себе\n" +
        "/cancel - скасувати поточний діалог\n" +
        "/new - створити заявку для всіх мешканців комплексу: будинок або прибудинкова територія → поверх → категорія → опис\n" +
        "/my - власні заявки\n/complex - відкриті заявки для мешканців комплексу\n" +
        "/request номер - деталі власної заявки\n/delete номер - видалити власну нову заявку з підтвердженням\n" +
        "Номер можна ввести одразу або наступним повідомленням після /request або /delete.\n\n" +
        "Для роботи із заявками завершіть реєстрацію. Фото та коментарі ще не підтримуються.";

    public async Task<CommandOutcome> HandleAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var navigation = await repository.GetPageNavigationAsync(envelope.UserId, cancellationToken);
        if (navigation is not null && (navigation.UserId != envelope.UserId || navigation.ComplexId != buildingId))
            throw new InvalidOperationException("Page navigation identity mismatch.");
        var activeNavigation = navigation is { NextCommand: not null } && navigation.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds();
        var command = BotCommand.Parse(envelope.Text, botUsername);
        var navigatingNext = false;
        if (command is null && envelope.Contact is null && activeNavigation &&
            await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
        {
            if (envelope.Text == "Скасувати")
                return new("Перегляд заявок завершено.", Keyboard: ReplyKeyboard.MainResident, PageNavigation: Navigation(navigation, envelope.UserId, null, returnToMenu: true));
            if (envelope.Text == "Далі")
            {
                envelope = envelope with { Text = navigation!.NextCommand! };
                command = BotCommand.Parse(envelope.Text, botUsername);
                navigatingNext = true;
            }
        }
        if (command is null && envelope.Contact is null)
        {
            var registered = await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken);
            var button = CommandMenu.CommandForButton(envelope.Text, registered);
            if (button is not null && !await MainKeyboardState.HasActiveDialogAsync(repository, envelope.UserId, buildingId, registered, cancellationToken,
                ignoreRequestDraft: navigation is { ReturnToMenu: true } && navigation.ExpiresAt > clock.GetUtcNow().ToUnixTimeSeconds()))
            {
                envelope = envelope with { Text = "/" + button };
                command = new BotCommand(button, "");
            }
        }
        var outcome = await HandleCoreAsync(envelope, cancellationToken);
        if (command is not null && command.Name is not ("edit_profile" or "delete_profile" or "account" or "cancel") && outcome.ProfileDialog is null)
            outcome = await profiles.ClearAsync(envelope.UserId, outcome, cancellationToken);
        if (command is not null && outcome.Lookup is null)
            outcome = await lookup.ClearAsync(envelope.UserId, outcome, cancellationToken);
        if (outcome.NextPageCommand is { } next)
            return outcome with { Keyboard = ReplyKeyboard.PageNavigation, PageNavigation = Navigation(navigation, envelope.UserId, next) };
        if (navigation is not null && command is not null && (navigation.NextCommand is not null || navigation.ReturnToMenu))
        {
            var returnToMenu = (navigatingNext || navigation.ReturnToMenu && command.Name != "new" &&
                outcome.Draft is null && outcome.Lookup is null && outcome.ProfileDialog is null) &&
                await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken);
            return outcome with
            {
                Keyboard = returnToMenu ? ReplyKeyboard.MainResident : outcome.Keyboard,
                PageNavigation = Navigation(navigation, envelope.UserId, null, returnToMenu: returnToMenu)
            };
        }
        return outcome;
    }

    private PageNavigationChange Navigation(PageNavigation? previous, long userId, string? next, bool returnToMenu = false) => new(
        new(userId, buildingId, next, (previous?.Version ?? 0) + 1, clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds(), returnToMenu), previous?.Version);

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
            case "register": // Compatibility alias for messages typed before the menu change.
                return await registrations.StartAsync(envelope.UserId, cancellationToken);
            case "cancel":
                return await profiles.CancelAsync(envelope.UserId, cancellationToken) ?? await lookup.CancelAsync(envelope.UserId, cancellationToken) ?? await requests.CancelAsync(envelope.UserId, cancellationToken) ?? await registrations.CancelAsync(envelope.UserId, cancellationToken);
            case "help":
                return new CommandOutcome(Help);
            case "contacts":
                return new CommandOutcome(DispatcherContacts);
            case "categories":
                return new CommandOutcome("Категорії заявок: " + Categories + ".");
            case not ("new" or "my" or "request" or "complex" or "delete" or "edit_profile" or "delete_profile" or "account"):
                return new CommandOutcome("Невідома команда. Перелік команд: /help.");
        }

        if (!await repository.IsResidentAsync(envelope.UserId, buildingId, cancellationToken))
            return await registrations.StatusAsync(envelope.UserId, cancellationToken);

        return command.Name switch
        {
            "account" => await profiles.StartAsync(envelope.UserId, ProfileAction.Account, cancellationToken),
            "edit_profile" => await profiles.StartAsync(envelope.UserId, ProfileAction.Edit, cancellationToken),
            "delete_profile" => await profiles.StartAsync(envelope.UserId, ProfileAction.Delete, cancellationToken),
            "new" => await requests.StartAsync(envelope, command.Arguments, cancellationToken),
            "complex" => await complexRequests.ListAsync(envelope.UserId, command.Arguments, cancellationToken),
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
        var lines = requests.Select(r => $"№{r.Id} - {StatusLabel(r.Status)}\n" + RequestListText.DateLine(r.CreatedAt) +
            RequestListText.LocationLine(r.Scope, r.BuildingNumber, r.Floor, r.Entrance) +
            RequestListText.Limit(r.Category, 80) + (string.IsNullOrEmpty(r.Location) ? "" : ": " + RequestListText.Limit(r.Location, 200)) +
            RequestListText.Description(r.Description) + $"\n/request {r.Id}").ToList();
        string? NextCommand(int count)
        {
            var next = count > 0 && count < requests.Count ? requests[count - 1].Id : page.NextBeforeId;
            return next is null ? null : "/my " + next.Value.ToString(CultureInfo.InvariantCulture);
        }
        string? nextCommand = null;
        string? CaptureNext(int count) => nextCommand = NextCommand(count);
        var reply = RequestListText.Build("Ваші заявки:", lines, CaptureNext);
        return new CommandOutcome(reply, NextPageCommand: nextCommand);
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
