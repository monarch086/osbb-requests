using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Commands;

public sealed class RequestCreationService(ICommandRepository repository, string complexId, TimeProvider clock)
{
    public const string Categories = "сантехніка, електрика, прибирання, місця загального користування, інше";
    public const string Usage = "Створити заявку для ЖК «Зелений острів 2»: /new.\n" +
        "Бот запитає будинок або весь комплекс, поверх (для будинку), категорію та опис. Скасувати: /cancel.\n\n" +
        "Або однією командою:\n/new 4В | 12 | сантехніка | Протікає труба\n" +
        "/new комплекс | - | електрика | Не працює освітлення\n\n" +
        "Будинки: 4Б, 4В, 4Г, 4Д. Поверхи: 1–25 або «Парадне»; «-» — без поверху. Категорії: " + Categories +
        ". Опис: 10–2000 символів. Заявка буде видимою для всіх мешканців комплексу.";
    public static bool IsActive(RequestDraft draft) => draft.Stage is not (RequestDraftStage.Completed or RequestDraftStage.Cancelled);

    public async Task<CommandOutcome> StartAsync(CommandEnvelope command, string arguments, CancellationToken ct)
    {
        var current = await repository.GetRequestDraftAsync(command.UserId, ct);
        if (current is not null && current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        if (arguments.Length > 0)
        {
            var parts = arguments.Split('|', 4, StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !TryScope(parts[0], out var scope, out var building) ||
                !TryFloor(parts[1], out var floor) || !ComplexLayout.IsValid(scope, building, floor) ||
                !ValidCategory(parts[2]) || parts[3].Length is < 10 or > 2000)
                return new(Usage);
            return Finish(command, new(command.UserId, complexId, RequestDraftStage.Description, current?.Version ?? 0,
                scope, building, floor, parts[2].ToLowerInvariant()), current?.Version, parts[3]);
        }
        if (current is { Stage: RequestDraftStage.Location })
        {
            var resumed = current with { Stage = RequestDraftStage.Description, Version = current.Version + 1 };
            return Prompt(resumed) with { Draft = new(resumed, current.Version) };
        }
        if (current is not null && IsActive(current)) return Prompt(current);
        var draft = new RequestDraft(command.UserId, complexId, RequestDraftStage.Scope, (current?.Version ?? 0) + 1);
        return Prompt(draft) with { Draft = new(draft, current?.Version) };
    }

    public async Task<CommandOutcome?> ContinueAsync(CommandEnvelope command, CancellationToken ct)
    {
        var current = await repository.GetRequestDraftAsync(command.UserId, ct);
        if (current is null || !IsActive(current)) return null;
        if (current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        if (command.Contact is not null) return Prompt(current);
        var text = command.Text.Trim();
        RequestDraft next;
        switch (current.Stage)
        {
            case RequestDraftStage.Scope:
                if (!TryScope(text, out var scope, out var building)) return Prompt(current);
                next = current with
                {
                    Scope = scope,
                    BuildingNumber = building,
                    Stage = scope == RequestScope.Complex ? RequestDraftStage.Category : RequestDraftStage.Floor
                };
                break;
            case RequestDraftStage.Floor:
                if (!TryFloor(text, out var floor)) return Prompt(current);
                next = current with { Floor = floor, Stage = RequestDraftStage.Category };
                break;
            case RequestDraftStage.Category:
                if (!ValidCategory(text)) return Prompt(current);
                next = current with { Category = text.ToLowerInvariant(), Stage = RequestDraftStage.Description };
                break;
            default:
                if (text.Length is < 10 or > 2000) return Prompt(current);
                return Finish(command, current, current.Version, text);
        }
        next = next with { Version = current.Version + 1 };
        return Prompt(next) with { Draft = new(next, current.Version) };
    }

    public async Task<CommandOutcome?> CancelAsync(long user, CancellationToken ct)
    {
        var current = await repository.GetRequestDraftAsync(user, ct);
        if (current is null || !IsActive(current)) return null;
        if (current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        return new("Створення заявки скасовано. Почати знову: /new.", Keyboard: ReplyKeyboard.Remove,
            Draft: new(new(user, complexId, RequestDraftStage.Cancelled, current.Version + 1), current.Version));
    }

    private CommandOutcome Finish(CommandEnvelope command, RequestDraft draft, int? expectedVersion, string description)
    {
        if (!ComplexLayout.IsValid(draft.Scope, draft.BuildingNumber, draft.Floor) || draft.Category is null)
            throw new InvalidOperationException("Incomplete request draft.");
        var request = new MaintenanceRequest(command.UpdateId, command.UserId, complexId, draft.Category, draft.Location ?? "",
            description, clock.GetUtcNow(), Visibility: RequestVisibility.Public, Scope: draft.Scope, BuildingNumber: draft.BuildingNumber, Floor: draft.Floor);
        return new($"Заявку №{request.Id} створено. Статус: Нова. Видимість: для всіх мешканців комплексу.\n" +
            ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor) + $"\nДеталі: /request {request.Id}",
            request, Keyboard: ReplyKeyboard.Remove,
            Draft: new(new(command.UserId, complexId, RequestDraftStage.Completed, (expectedVersion ?? 0) + 1), expectedVersion));
    }

    private static bool TryScope(string text, out RequestScope scope, out string? building)
    {
        var value = text.Trim().ToLowerInvariant();
        scope = RequestScope.Building; building = null;
        if (value is "комплекс" or "весь комплекс") { scope = RequestScope.Complex; return true; }
        value = value.Replace("будинок", "", StringComparison.Ordinal).Replace("№", "", StringComparison.Ordinal).Trim();
        var number = value.ToUpperInvariant();
        if (!ComplexLayout.BuildingNumbers.Contains(number, StringComparer.Ordinal)) return false;
        building = number; return true;
    }
    private static bool TryFloor(string text, out int? floor)
    {
        floor = null;
        if (text.Equals("Парадне", StringComparison.OrdinalIgnoreCase)) { floor = ComplexLayout.EntranceFloor; return true; }
        if (text is "-" || text.Equals("Без поверху", StringComparison.OrdinalIgnoreCase)) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > ComplexLayout.FloorCount)
            return false;
        floor = number; return true;
    }
    private static bool ValidCategory(string text) => Categories.Split(", ").Contains(text.ToLowerInvariant(), StringComparer.Ordinal);
    private static CommandOutcome Prompt(RequestDraft draft) => draft.Stage switch
    {
        RequestDraftStage.Scope => new("ЖК «Зелений острів 2».\n" +
            "Де виникла проблема? Оберіть будинок 4Б, 4В, 4Г або 4Д або «Весь комплекс». Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestScope),
        RequestDraftStage.Floor => new($"Будинок {draft.BuildingNumber}. Вкажіть поверх від 1 до 25, оберіть «Парадне» або «Без поверху» " +
            "для фасаду, підвалу чи проблеми всього будинку. Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestFloor),
        RequestDraftStage.Category => new("Оберіть категорію: " + Categories + ". Заявка буде видимою для всіх мешканців комплексу. Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestCategory),
        _ => new("Опишіть проблему (10–2000 символів). Після цього заявку буде створено для всіх мешканців комплексу. Скасувати: /cancel.", Keyboard: ReplyKeyboard.Remove)
    };
}
