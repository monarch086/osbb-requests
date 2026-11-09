using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Domain;
using OsbbRequests.Shared.Models;

namespace OsbbRequests.Bot.Commands;

public sealed class RequestCreationService(ICommandRepository repository, string complexId, TimeProvider clock)
{
    public const string Categories = "Електрика МЗК, Сантехніка МЗК, Ліфти, Прибирання МЗК, Інше";
    public const string Usage = "Створити заявку для ЖК «Зелений острів 2»: /new.\n" +
        "Бот запитає будинок або прибудинкову територію, під’їзд та поверх (для будинку), категорію та опис. Скасувати: /cancel.\n\n" +
        "Або однією командою:\n/new 4В | 2 | 12 | Сантехніка МЗК | Протікає труба\n" +
        "/new комплекс | - | Електрика МЗК | Не працює освітлення\n\n" +
        "Під’їзди: 1, 2 або «Не зазначено»; для 4Б автоматично 1.\n" +
        "Будинки: 4Б, 4В, 4Г, 4Д. Поверхи: 1–25 або «Парадне»; «-» - без поверху. Категорії: " + Categories +
        ". Опис: 10–2000 символів. Заявка буде видимою для всіх мешканців комплексу.";
    public static bool IsActive(RequestDraft draft) => draft.Stage is not (RequestDraftStage.Completed or RequestDraftStage.Cancelled);

    public async Task<CommandOutcome> StartAsync(CommandEnvelope command, string arguments, CancellationToken ct)
    {
        var owner = await OwnerAsync(command.UserId, ct);
        var current = await repository.GetRequestDraftAsync(command.UserId, ct);
        if (current is not null && current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        if (arguments.Length > 0)
        {
            var parts = arguments.Split('|', 5, StringSplitOptions.TrimEntries);
            var withEntrance = parts.Length == 5 && TryFloor(parts[2], out _) && ValidCategory(parts[3]);
            if (!withEntrance) parts = arguments.Split('|', 4, StringSplitOptions.TrimEntries);
            var floorIndex = withEntrance ? 2 : 1;
            int? entrance = null;
            if (parts.Length != (withEntrance ? 5 : 4) || !TryScope(parts[0], out var scope, out var building) ||
                (withEntrance && !TryEntrance(parts[1], out entrance)) ||
                !TryFloor(parts[floorIndex], out var floor) || !ComplexLayout.IsValid(scope, building, floor, entrance) ||
                !ValidCategory(parts[floorIndex + 1]) || parts[floorIndex + 2].Length is < 10 or > 2000)
                return new(Usage);
            if (building == "4Б") entrance = 1;
            return Finish(command, new(command.UserId, complexId, RequestDraftStage.Description, current?.Version ?? 0,
                scope, building, floor, CanonicalCategory(parts[floorIndex + 1])!, Entrance: entrance), current?.Version, parts[floorIndex + 2]);
        }
        if (current is { Stage: RequestDraftStage.Location })
        {
            var resumed = current with { Stage = RequestDraftStage.Description, Version = current.Version + 1 };
            return Prompt(resumed) with { Draft = new(resumed, current.Version) };
        }
        if (current is not null && IsActive(current)) return Prompt(current, owner is not null);
        var draft = new RequestDraft(command.UserId, complexId, RequestDraftStage.Scope, (current?.Version ?? 0) + 1);
        return Prompt(draft) with { Draft = new(draft, current?.Version) };
    }

    public async Task<CommandOutcome?> ContinueAsync(CommandEnvelope command, CancellationToken ct)
    {
        var current = await repository.GetRequestDraftAsync(command.UserId, ct);
        if (current is null || !IsActive(current)) return null;
        if (current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        var owner = await OwnerAsync(command.UserId, ct);
        if ((current.Category == RequestCategories.KeyOrder || current.Stage is RequestDraftStage.KeyType or RequestDraftStage.KeyQuantity) && owner is null)
            return new("Замовлення ключів доступне лише підтвердженим власникам. Скасувати: /cancel.");
        if (command.Contact is not null) return Prompt(current, owner is not null);
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
                    Entrance = building == "4Б" ? 1 : null,
                    Stage = scope == RequestScope.Complex ? RequestDraftStage.Category : building == "4Б" ? RequestDraftStage.Floor : RequestDraftStage.Entrance
                };
                break;
            case RequestDraftStage.Entrance:
                if (!TryEntrance(text, out var entrance)) return Prompt(current);
                next = current with { Entrance = entrance, Stage = RequestDraftStage.Floor };
                break;
            case RequestDraftStage.Floor:
                if (!TryFloor(text, out var floor)) return Prompt(current);
                next = current with { Floor = floor, Stage = RequestDraftStage.Category };
                break;
            case RequestDraftStage.Category:
                if (text.Equals(RequestCategories.KeyOrder, StringComparison.OrdinalIgnoreCase))
                {
                    if (owner is null) return Prompt(current, false);
                    next = current with { Category = RequestCategories.KeyOrder, Stage = RequestDraftStage.KeyType };
                }
                else
                {
                    if (!ValidCategory(text)) return Prompt(current, owner is not null);
                    next = current with { Category = CanonicalCategory(text), Stage = RequestDraftStage.Description };
                }
                break;
            case RequestDraftStage.KeyType:
                var keyType = text.ToLowerInvariant() switch { "магніт" => "Магніт", "перепустка" => "Перепустка", _ => null };
                if (keyType is null) return Prompt(current, true);
                next = current with { KeyType = keyType, Stage = RequestDraftStage.KeyQuantity };
                break;
            case RequestDraftStage.KeyQuantity:
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity < 1)
                    return Prompt(current, true);
                return FinishKeyOrder(command, current, owner!, quantity);
            default:
                if (text.Length is < 10 or > 2000) return Prompt(current);
                return Finish(command, current, current.Version, text);
        }
        next = next with { Version = current.Version + 1 };
        return Prompt(next, owner is not null) with { Draft = new(next, current.Version) };
    }

    public async Task<CommandOutcome?> CancelAsync(long user, CancellationToken ct)
    {
        var current = await repository.GetRequestDraftAsync(user, ct);
        if (current is null || !IsActive(current)) return null;
        if (current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        return new("Створення заявки скасовано. Почати знову: /new.", Keyboard: ReplyKeyboard.Remove,
            Draft: new(new(user, complexId, RequestDraftStage.Cancelled, current.Version + 1), current.Version));
    }

    private async Task<ResidentRegistration?> OwnerAsync(long userId, CancellationToken ct)
    {
        var profile = await repository.GetRegistrationAsync(userId, ct);
        return profile is { Stage: RegistrationStage.Approved, ResidentType: ResidentType.Owner, PersonalDataDeleted: false } && profile.BuildingId == complexId && profile.UserId == userId ? profile : null;
    }

    private CommandOutcome FinishKeyOrder(CommandEnvelope command, RequestDraft draft, ResidentRegistration owner, int quantity)
    {
        var entrance = draft.BuildingNumber == "4Б" ? 1 : draft.Entrance;
        if (draft.Category != RequestCategories.KeyOrder || draft.KeyType is not ("Магніт" or "Перепустка") ||
            !ComplexLayout.IsValid(draft.Scope, draft.BuildingNumber, draft.Floor, entrance)) throw new InvalidOperationException("Incomplete key order.");
        var description = $"Тип ключа: {draft.KeyType}\nКількість: {quantity}";
        var request = new MaintenanceRequest(command.UpdateId, command.UserId, complexId, RequestCategories.KeyOrder, "", description,
            clock.GetUtcNow(), Visibility: RequestVisibility.Private, Scope: draft.Scope, BuildingNumber: draft.BuildingNumber,
            Floor: draft.Floor, Entrance: entrance, KeyType: draft.KeyType, KeyQuantity: quantity);
        return new($"Замовлення №{request.Id} створено. Статус: Нова. Видимість: приватна.\n{description}\nДеталі: /request {request.Id}",
            request, Keyboard: ReplyKeyboard.Remove, Draft: new(new(command.UserId, complexId, RequestDraftStage.Completed, draft.Version + 1), draft.Version),
            OwnerProfileVersion: owner.Version);
    }

    private CommandOutcome Finish(CommandEnvelope command, RequestDraft draft, int? expectedVersion, string description)
    {
        var entrance = draft.BuildingNumber == "4Б" ? 1 : draft.Entrance;
        if (!ComplexLayout.IsValid(draft.Scope, draft.BuildingNumber, draft.Floor, entrance) || draft.Category is null)
            throw new InvalidOperationException("Incomplete request draft.");
        var request = new MaintenanceRequest(command.UpdateId, command.UserId, complexId, CanonicalCategory(draft.Category) ?? throw new InvalidOperationException("Unknown request category."), draft.Location ?? "",
            description, clock.GetUtcNow(), Visibility: RequestVisibility.Public, Scope: draft.Scope, BuildingNumber: draft.BuildingNumber, Floor: draft.Floor, Entrance: entrance);
        return new($"Заявку №{request.Id} створено. Статус: Нова. Видимість: для всіх мешканців комплексу.\n" +
            ComplexLayout.Label(request.Scope, request.BuildingNumber, request.Floor, request.Entrance) + $"\nДеталі: /request {request.Id}",
            request, Keyboard: ReplyKeyboard.Remove,
            Draft: new(new(command.UserId, complexId, RequestDraftStage.Completed, (expectedVersion ?? 0) + 1), expectedVersion));
    }

    private static bool TryScope(string text, out RequestScope scope, out string? building)
    {
        var value = text.Trim().ToLowerInvariant();
        scope = RequestScope.Building; building = null;
        if (value is "комплекс" or "весь комплекс" or "весь комплекс та прибудинкова територія" || value.Equals(ComplexLayout.ComplexLabel, StringComparison.OrdinalIgnoreCase)) { scope = RequestScope.Complex; return true; }
        value = value.Replace("будинок", "", StringComparison.Ordinal).Replace("№", "", StringComparison.Ordinal).Trim();
        var number = value.ToUpperInvariant();
        if (!ComplexLayout.BuildingNumbers.Contains(number, StringComparer.Ordinal)) return false;
        building = number; return true;
    }
    private static bool TryEntrance(string text, out int? entrance)
    {
        entrance = null;
        if (text is "-" || text.Equals("Не зазначено", StringComparison.OrdinalIgnoreCase)) return true;
        if (text is not ("1" or "2")) return false;
        entrance = int.Parse(text, CultureInfo.InvariantCulture);
        return true;
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
    private static bool ValidCategory(string text) => CanonicalCategory(text) is not null;
    private static string? CanonicalCategory(string text) => text.Trim().ToLowerInvariant() switch
    {
        "електрика" or "електрика мзк" => "Електрика МЗК",
        "сантехніка" or "сантехніка мзк" => "Сантехніка МЗК",
        "ліфт" or "ліфти" => "Ліфти",
        "прибирання" or "прибирання мзк" => "Прибирання МЗК",
        "інше" or "місця загального користування" => "Інше",
        _ => null
    };
    private static CommandOutcome Prompt(RequestDraft draft, bool owner = false) => draft.Stage switch
    {
        RequestDraftStage.Scope => new("ЖК «Зелений острів 2».\n" +
            "Де виникла проблема? Оберіть будинок 4Б, 4В, 4Г або 4Д або «" + ComplexLayout.ComplexLabel + "». Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestScope),
        RequestDraftStage.Entrance => new($"Будинок {draft.BuildingNumber}. Оберіть під’їзд: 1, 2 або «Не зазначено». Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestEntrance),
        RequestDraftStage.Floor => new($"Будинок {draft.BuildingNumber}. Оберіть поверх від 1 до 25, «Парадне» або «Без поверху» " +
            "для фасаду, підвалу чи проблеми всього будинку. Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestFloor),
        RequestDraftStage.Category => new("Оберіть категорію: " + Categories + (owner ? ", " + RequestCategories.KeyOrder : "") + ". Скасувати: /cancel.", Keyboard: owner ? ReplyKeyboard.OwnerRequestCategory : ReplyKeyboard.RequestCategory),
        RequestDraftStage.KeyType => new("Оберіть тип ключа: Магніт або Перепустка. Скасувати: /cancel.", Keyboard: ReplyKeyboard.KeyType),
        RequestDraftStage.KeyQuantity => new("Введіть кількість ключів цілим додатним числом. Скасувати: /cancel.", Keyboard: ReplyKeyboard.Remove),
        _ => new("Опишіть проблему (10–2000 символів). Після цього заявку буде створено для всіх мешканців комплексу. Скасувати: /cancel.", Keyboard: ReplyKeyboard.Remove)
    };
}
