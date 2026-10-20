using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class RequestLookupService(ICommandRepository repository, string complexId)
{
    private const string Prompt = "Введіть номер заявки. Скасувати: /cancel.";

    public async Task<CommandOutcome> StartAsync(long userId, string arguments, CancellationToken ct,
        RequestLookupAction action = RequestLookupAction.View)
    {
        if (TryId(arguments, out var id))
        {
            if (action == RequestLookupAction.View) return await DetailAsync(userId, arguments, ct);
            return await PrepareDeleteAsync(userId, id, await GetAsync(userId, ct), ct);
        }
        var current = await GetAsync(userId, ct);
        var next = new RequestLookup(userId, complexId, true, (current?.Version ?? 0) + 1, action);
        return new(NumberPrompt(action), Keyboard: ReplyKeyboard.Remove, Lookup: new(next, current?.Version));
    }

    public async Task<CommandOutcome?> ContinueAsync(CommandEnvelope command, CancellationToken ct)
    {
        var current = await GetAsync(command.UserId, ct);
        if (current is not { IsActive: true }) return null;
        if (current.RequestId is { } requestId)
        {
            if (command.Contact is not null || !command.Text.Trim().Equals("Так", StringComparison.OrdinalIgnoreCase))
                return new(Confirmation(requestId), Keyboard: ReplyKeyboard.Remove);
            var request = await repository.GetDeletableRequestAsync(command.UserId, requestId, ct);
            if (request is null || request.OwnerId != command.UserId || request.ComplexId != complexId)
                return Clear(current, new("Заявку не знайдено серед ваших заявок."));
            if (request.Status != RequestStatus.New)
                return Clear(current, new("Видалити можна лише власну заявку зі статусом «Нова»."));
            if (request.Version != current.RequestVersion)
                return Clear(current, new("Заявку змінено. Перегляньте її та повторіть /delete номер."));
            return Clear(current, new($"Заявку №{requestId} видалено.",
                Deletion: new(requestId, command.UserId, complexId, request.Version)));
        }
        if (command.Contact is not null || !TryId(command.Text.Trim(), out var id))
            return new(NumberPrompt(current.Action), Keyboard: ReplyKeyboard.Remove);
        if (current.Action == RequestLookupAction.Delete)
            return await PrepareDeleteAsync(command.UserId, id, current, ct);
        var outcome = await DetailAsync(command.UserId, command.Text.Trim(), ct);
        return Clear(current, outcome);
    }

    private async Task<CommandOutcome> PrepareDeleteAsync(long userId, long id, RequestLookup? current, CancellationToken ct)
    {
        var request = await repository.GetDeletableRequestAsync(userId, id, ct);
        CommandOutcome? error = request is null || request.OwnerId != userId || request.ComplexId != complexId
            ? new("Заявку не знайдено серед ваших заявок.")
            : request.Status != RequestStatus.New ? new("Видалити можна лише власну заявку зі статусом «Нова».") : null;
        if (error is not null) return current is { IsActive: true } ? Clear(current, error) : error;
        var next = new RequestLookup(userId, complexId, false, (current?.Version ?? 0) + 1,
            RequestLookupAction.Delete, id, request!.Version);
        return new(Confirmation(id), Keyboard: ReplyKeyboard.Remove, Lookup: new(next, current?.Version));
    }

    private static string NumberPrompt(RequestLookupAction action) => action == RequestLookupAction.Delete
        ? "Введіть номер власної заявки для видалення (лише статус «Нова»). Скасувати: /cancel." : Prompt;
    private static string Confirmation(long id) => $"Видалити вашу заявку №{id}? Надішліть «Так» для підтвердження або /cancel для скасування.";

    public async Task<CommandOutcome?> CancelAsync(long userId, CancellationToken ct)
    {
        var current = await GetAsync(userId, ct);
        return current is { IsActive: true }
            ? Clear(current, new("Дію із заявкою скасовано. Ваші заявки: /my.", Keyboard: ReplyKeyboard.Remove)) : null;
    }

    public async Task<CommandOutcome> ClearAsync(long userId, CommandOutcome outcome, CancellationToken ct)
    {
        var current = await GetAsync(userId, ct);
        return current is { IsActive: true } ? Clear(current, outcome) : outcome;
    }

    private async Task<RequestLookup?> GetAsync(long userId, CancellationToken ct)
    {
        var current = await repository.GetRequestLookupAsync(userId, ct);
        if (current is not null && (current.ComplexId != complexId || current.UserId != userId))
            throw new InvalidOperationException("Lookup identity mismatch.");
        return current;
    }

    private static CommandOutcome Clear(RequestLookup current, CommandOutcome outcome) => outcome with
    {
        Lookup = new(current with
        {
            AwaitingNumber = false,
            RequestId = null,
            RequestVersion = null,
            Action = RequestLookupAction.View,
            Version = current.Version + 1
        }, current.Version)
    };

    private async Task<CommandOutcome> DetailAsync(long userId, string arguments, CancellationToken cancellationToken)
    {
        if (!TryId(arguments, out var id))
            return new CommandOutcome(Prompt);
        var request = await repository.GetRequestAsync(userId, id, cancellationToken);
        if (request is null || request.BuildingId != complexId)
            return new CommandOutcome("Заявку не знайдено серед ваших заявок.");
        var localDate = TimeZoneInfo.ConvertTime(request.CreatedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv"));
        var text = $"Заявка №{request.Id}\nСтатус: {CommandHandler.StatusLabel(request.Status)}\n" +
            $"{ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor)}\nКатегорія: {request.Category}\n" +
            (string.IsNullOrEmpty(request.Location) ? "" : $"Місце: {request.Location}\n") +
            $"Подано: {localDate:dd.MM.yyyy HH:mm} (Київ)\n\n{request.Description}";
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            text += "\n\nПояснення адміністратора: " + request.Resolution;
        return new CommandOutcome(text);
    }

    private static bool TryId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 0;
}
