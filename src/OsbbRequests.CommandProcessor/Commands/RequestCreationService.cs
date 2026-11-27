using System.Globalization;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Persistence;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Domain.Residents;
using OsbbRequests.Services.Requests;
using OsbbRequests.Services.Residents;

namespace OsbbRequests.CommandProcessor.Commands;

public sealed class RequestCreationService(ICommandRepository repository, string complexId, TimeProvider clock)
{
    private const string FinishKeyOrderButton = "Завершити створення заявки";
    public const string Categories = "Електрика МЗК, Сантехніка МЗК, Ліфти, Прибирання МЗК, Інше";
    public const string Usage = "Створити заявку для ЖК «Зелений острів 2»: /new.\n" +
        "Бот запитає категорію, будинок або прибудинкову територію, під’їзд та поверх (для будинку) і опис. Скасувати: /cancel.\n\n" +
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
            {
                return new(Usage);
            }

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
        var draft = new RequestDraft(command.UserId, complexId, RequestDraftStage.InitialCategory, (current?.Version ?? 0) + 1);
        return Prompt(draft, owner is not null) with { Draft = new(draft, current?.Version) };
    }

    public async Task<CommandOutcome?> ContinueAsync(CommandEnvelope command, CancellationToken ct)
    {
        var current = await repository.GetRequestDraftAsync(command.UserId, ct);
        if (current is null || !IsActive(current)) return null;
        if (current.ComplexId != complexId) throw new InvalidOperationException("Draft complex mismatch.");
        var owner = await OwnerAsync(command.UserId, ct);
        return await ContinueDraftAsync(command, current, owner, ct);
    }

    private async Task<CommandOutcome> ContinueDraftAsync(CommandEnvelope command, RequestDraft draft,
        ResidentRegistration? owner, CancellationToken ct)
    {
        var keyStep = IsKeyStep(draft.Stage);
        if ((keyStep || draft.Category == RequestCategories.KeyOrder) && owner is null)
            return new("Замовлення ключів доступне лише підтвердженим власникам. Скасувати: /cancel.");
        if (command.Contact is not null) return Prompt(draft, owner is not null);
        var text = command.Text.Trim();
        return keyStep ? await ContinueKeyOrderAsync(command, draft, text, owner!, ct) :
            ContinueRequest(command, draft, text, owner is not null);
    }

    private static bool IsKeyStep(RequestDraftStage stage) =>
        stage is RequestDraftStage.KeyType or RequestDraftStage.KeyQuantity or RequestDraftStage.KeyPhone or RequestDraftStage.KeyNext;

    private CommandOutcome ContinueRequest(CommandEnvelope command, RequestDraft draft, string text, bool owner) => draft.Stage switch
    {
        RequestDraftStage.InitialCategory or RequestDraftStage.Category => SelectCategory(draft, text, owner),
        RequestDraftStage.Scope or RequestDraftStage.Entrance or RequestDraftStage.Floor => SelectLocation(draft, text, owner),
        _ => CompleteDescription(command, draft, text)
    };

    private CommandOutcome CompleteDescription(CommandEnvelope command, RequestDraft draft, string text) =>
        text.Length is < 10 or > 2000 ? Prompt(draft) : Finish(command, draft, draft.Version, text);

    private Task<CommandOutcome> ContinueKeyOrderAsync(CommandEnvelope command, RequestDraft draft, string text,
        ResidentRegistration owner, CancellationToken ct) => draft.Stage switch
        {
            RequestDraftStage.KeyType => Task.FromResult(SelectKeyType(draft, text)),
            RequestDraftStage.KeyNext => Task.FromResult(SelectNextKey(command, draft, text, owner)),
            // Existing unfinished orders resume at the new phone step.
            _ => EnterKeyPhoneAsync(command, draft, text, owner, ct)
        };

    private static CommandOutcome SelectCategory(RequestDraft draft, string text, bool owner)
    {
        if (text.Equals(RequestCategories.KeyOrder, StringComparison.OrdinalIgnoreCase))
        {
            return owner ? Advance(draft, draft with { Category = RequestCategories.KeyOrder, Stage = RequestDraftStage.KeyType }, owner)
                : Prompt(draft);
        }

        if (!ValidCategory(text)) return Prompt(draft, owner);
        return Advance(draft, draft with
        {
            Category = CanonicalCategory(text),
            Stage = draft.Stage == RequestDraftStage.InitialCategory ? RequestDraftStage.Scope : RequestDraftStage.Description
        }, owner);
    }

    private static CommandOutcome SelectLocation(RequestDraft draft, string text, bool owner) => draft.Stage switch
    {
        RequestDraftStage.Scope => SelectScope(draft, text, owner),
        RequestDraftStage.Entrance when TryEntrance(text, out var entrance) =>
            Advance(draft, draft with { Entrance = entrance, Stage = RequestDraftStage.Floor }, owner),
        RequestDraftStage.Floor when TryFloor(text, out var floor) =>
            Advance(draft, draft with { Floor = floor, Stage = AfterLocation(draft) }, owner),
        _ => Prompt(draft)
    };

    private static CommandOutcome SelectScope(RequestDraft draft, string text, bool owner)
    {
        if (!TryScope(text, out var scope, out var building)) return Prompt(draft);
        return Advance(draft, draft with
        {
            Scope = scope,
            BuildingNumber = building,
            Entrance = building == "4Б" ? 1 : null,
            Stage = scope == RequestScope.Complex ? AfterLocation(draft) : building == "4Б" ? RequestDraftStage.Floor : RequestDraftStage.Entrance
        }, owner);
    }

    private static RequestDraftStage AfterLocation(RequestDraft draft) =>
        draft.Category is null ? RequestDraftStage.Category : RequestDraftStage.Description;

    private static CommandOutcome SelectKeyType(RequestDraft draft, string text)
    {
        var keyType = text.ToLowerInvariant() switch { "магніт" => "Магніт", "перепустка" => "Перепустка", _ => null };
        return keyType is null ? Prompt(draft, true) :
            Advance(draft, draft with { KeyType = keyType, Stage = RequestDraftStage.KeyPhone }, true);
    }

    private async Task<CommandOutcome> EnterKeyPhoneAsync(CommandEnvelope command, RequestDraft draft, string text,
        ResidentRegistration owner, CancellationToken ct)
    {
        var keyboard = KeyPhoneKeyboard(draft);
        if (keyboard == ReplyKeyboard.KeyFinish && text.Equals(FinishKeyOrderButton, StringComparison.OrdinalIgnoreCase))
            return FinishKeyOrder(command, draft, owner);
        if (!PhoneNumber.TryNormalize(text, out var phone))
            return new("Введіть коректний номер телефону, наприклад 0671234567 або +380671234567. Скасувати: /cancel.", Keyboard: keyboard);
        if (AlreadyAdded(draft, phone!))
            return new("Цей номер уже додано до заявки. Введіть інший номер, завершіть створення заявки або скасуйте її.", Keyboard: ReplyKeyboard.KeyFinish);
        if (!await repository.ResidentPhoneExistsAsync(complexId, phone!, ct))
            return new("Мешканця з таким номером телефону не знайдено. Введіть номер ще раз або скасуйте створення заявки.", Keyboard: keyboard);
        var items = (draft.KeyItems ?? []).Append(new KeyOrderItem(draft.KeyType!, phone!)).ToArray();
        return Advance(draft, draft with { KeyItems = items, Stage = RequestDraftStage.KeyNext }, true);
    }

    private static bool AlreadyAdded(RequestDraft draft, string phone) =>
        draft.KeyItems?.Any(item => PhoneNumber.TryNormalize(item.Phone, out var existing) && existing == phone) == true;

    private CommandOutcome SelectNextKey(CommandEnvelope command, RequestDraft draft, string text, ResidentRegistration owner)
    {
        if (text.Equals(FinishKeyOrderButton, StringComparison.OrdinalIgnoreCase))
            return FinishKeyOrder(command, draft, owner);
        if (!text.Equals("Додати ще один ключ", StringComparison.OrdinalIgnoreCase) || (draft.KeyItems?.Count ?? 0) >= 30)
            return Prompt(draft, true);
        return Advance(draft, draft with { KeyType = null, Stage = RequestDraftStage.KeyType }, true);
    }

    private static CommandOutcome Advance(RequestDraft current, RequestDraft next, bool owner)
    {
        next = next with { Version = current.Version + 1 };
        return Prompt(next, owner) with { Draft = new(next, current.Version) };
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
        return profile is not null && profile.IsApprovedOwner() && profile.BuildingId == complexId && profile.UserId == userId ? profile : null;
    }

    private CommandOutcome FinishKeyOrder(CommandEnvelope command, RequestDraft draft, ResidentRegistration owner)
    {
        var items = draft.KeyItems;
        if (draft.Category != RequestCategories.KeyOrder || items is null || items.Count is < 1 or > 30 ||
            items.Any(item => item.Type is not ("Магніт" or "Перепустка") || !PhoneNumber.TryNormalize(item.Phone, out _)))
        {
            throw new InvalidOperationException("Incomplete key order.");
        }

        var unique = items.DistinctBy(item => PhoneNumber.Normalize(item.Phone), StringComparer.Ordinal).ToArray();
        if (unique.Length != items.Count)
        {
            var cleaned = draft with { KeyItems = unique, Stage = RequestDraftStage.KeyNext, Version = draft.Version + 1 };
            return new("У чернетці були повтори. Для кожного номера залишено перший ключ. Перевірте замовлення: додайте ключ для іншого номера, завершіть створення заявки або скасуйте її.",
                Keyboard: ReplyKeyboard.KeyNext, Draft: new(cleaned, draft.Version));
        }
        var description = string.Join("\n", items.Select((item, index) => $"{index + 1}. {item.Type} — {item.Phone}"));
        var request = new MaintenanceRequest(command.UpdateId, command.UserId, complexId, RequestCategories.KeyOrder, "", description,
            clock.GetUtcNow(), Visibility: RequestVisibility.Private, KeyQuantity: items.Count, KeyItems: items);
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
            RequestLocationText.Format(request.Scope, request.BuildingNumber, request.Floor, request.Entrance) + $"\nДеталі: /request {request.Id}",
            request, Keyboard: ReplyKeyboard.Remove,
            Draft: new(new(command.UserId, complexId, RequestDraftStage.Completed, (expectedVersion ?? 0) + 1), expectedVersion));
    }

    private static bool TryScope(string text, out RequestScope scope, out string? building)
    {
        var value = text.Trim().ToLowerInvariant();
        scope = RequestScope.Building; building = null;
        if (value is "комплекс" or "весь комплекс" or "весь комплекс та прибудинкова територія" || value.Equals(RequestLocationText.ComplexLabel, StringComparison.OrdinalIgnoreCase)) { scope = RequestScope.Complex; return true; }
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
    private static ReplyKeyboard KeyPhoneKeyboard(RequestDraft draft) =>
        draft.KeyItems is { Count: > 0 } ? ReplyKeyboard.KeyFinish : ReplyKeyboard.Cancel;

    private static CommandOutcome Prompt(RequestDraft draft, bool owner = false) => draft.Stage switch
    {
        RequestDraftStage.Scope => new("ЖК «Зелений острів 2».\n" +
            "Де виникла проблема? Оберіть будинок 4Б, 4В, 4Г або 4Д або «" + RequestLocationText.ComplexLabel + "». Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestScope),
        RequestDraftStage.Entrance => new($"Будинок {draft.BuildingNumber}. Оберіть під’їзд: 1, 2 або «Не зазначено». Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestEntrance),
        RequestDraftStage.Floor => new($"Будинок {draft.BuildingNumber}. Оберіть поверх від 1 до 25. Скасувати: /cancel.", Keyboard: ReplyKeyboard.RequestFloor),
        RequestDraftStage.InitialCategory or RequestDraftStage.Category => new("Оберіть категорію: " + Categories + (owner ? ", " + RequestCategories.KeyOrder : "") + ". Скасувати: /cancel.", Keyboard: owner ? ReplyKeyboard.OwnerRequestCategory : ReplyKeyboard.RequestCategory),
        RequestDraftStage.KeyType => new("Оберіть тип ключа: Магніт або Перепустка. Скасувати: /cancel.", Keyboard: ReplyKeyboard.KeyType),
        RequestDraftStage.KeyQuantity or RequestDraftStage.KeyPhone => new("Введіть номер телефону зареєстрованого мешканця, до якого прив’язати ключ. Скасувати: /cancel.", Keyboard: KeyPhoneKeyboard(draft)),
        RequestDraftStage.KeyNext => new((draft.KeyItems?.Count >= 30 ? "Досягнуто межі 30 ключів. " : "Ключ додано. ") + "Завершіть створення заявки або додайте ще один ключ.", Keyboard: draft.KeyItems?.Count >= 30 ? ReplyKeyboard.KeyFinish : ReplyKeyboard.KeyNext),
        _ => new("Опишіть проблему (10–2000 символів). Після цього заявку буде створено для всіх мешканців комплексу. Скасувати: /cancel.", Keyboard: ReplyKeyboard.Remove)
    };
}
