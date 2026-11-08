using System.Globalization;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;

namespace OsbbRequests.Bot.Commands;

public sealed class ResidentProfileService(ICommandRepository repository, string complexId, TimeProvider clock)
{
    public const string DeletedLabel = "Мешканець видалив свої дані";
    public const string Confirmation = "Видалити мої дані";
    public async Task<CommandOutcome> StartAsync(long userId, ProfileAction action, CancellationToken ct)
    {
        var current = await repository.GetRegistrationAsync(userId, ct);
        if (current is not null && current.BuildingId != complexId) throw new InvalidOperationException("Profile complex mismatch.");
        var previous = await repository.GetProfileDialogAsync(userId, ct);
        Validate(previous, userId);
        if (previous is { Active: true, Draft: { } draft } && previous.Action == action)
            return action == ProfileAction.Account ? AccountPrompt(draft.Stage) : EditPrompt(draft.Stage);
        if (action == ProfileAction.Account)
        {
            if (current is null || current.Stage != RegistrationStage.Approved || current.PersonalDataDeleted)
                return new("Дані профілю недоступні. Зверніться до адміністратора ОСББ.");
            if (TryAccount(current, out var account))
            {
                var result = new CommandOutcome("Ваш особовий рахунок: № " + account, Keyboard: ReplyKeyboard.Remove);
                return previous is { Active: true } ? result with { ProfileDialog = Close(previous, "").ProfileDialog } : result;
            }
            var stage = ValidBuilding(current.BuildingNumber) ? RegistrationStage.AwaitingApartment : RegistrationStage.AwaitingBuilding;
            var accountDraft = current with { Stage = stage };
            var accountDialog = new ResidentProfileDialog(userId, complexId, action, (previous?.Version ?? 0) + 1, current.Version, accountDraft);
            return AccountPrompt(stage) with { ProfileDialog = new(accountDialog, previous?.Version) };
        }
        var now = clock.GetUtcNow();
        var value = new ResidentRegistration(current?.Id ?? Guid.NewGuid().ToString("N"), userId, complexId,
            RegistrationStage.AwaitingPhone, 1, current?.CreatedAt ?? now, now, PersonalDataDeletedAt: current?.PersonalDataDeletedAt, ApprovedAt: current?.ApprovedAt ?? current?.UpdatedAt ?? now, ConsentAt: current?.ConsentAt, ConsentVersion: current?.ConsentVersion);
        var dialog = new ResidentProfileDialog(userId, complexId, action, (previous?.Version ?? 0) + 1, current?.Version,
            action == ProfileAction.Edit ? value : null);
        var prompt = action == ProfileAction.Edit ? EditPrompt(value.Stage) : DeletePrompt();
        return prompt with { ProfileDialog = new(dialog, previous?.Version) };
    }
    public async Task<CommandOutcome?> CancelAsync(long userId, CancellationToken ct)
    {
        var dialog = await repository.GetProfileDialogAsync(userId, ct);
        Validate(dialog, userId);
        return dialog is { Active: true } ? Close(dialog, "Дію з профілем скасовано. Попередні дані збережено.") : null;
    }
    public async Task<CommandOutcome> ClearAsync(long userId, CommandOutcome outcome, CancellationToken ct)
    {
        var cancelled = await CancelAsync(userId, ct);
        return cancelled is null ? outcome : outcome with { ProfileDialog = cancelled.ProfileDialog, Keyboard = outcome.Keyboard ?? ReplyKeyboard.Remove };
    }
    public async Task<CommandOutcome?> ContinueAsync(CommandEnvelope command, CancellationToken ct)
    {
        var dialog = await repository.GetProfileDialogAsync(command.UserId, ct);
        if (dialog?.Active != true) return null;
        if (dialog.UserId != command.UserId || dialog.ComplexId != complexId) throw new InvalidOperationException("Profile dialog identity mismatch.");
        if (command.Text.Trim() == "Скасувати") return Close(dialog, "Дію скасовано. Попередні дані збережено.");
        var current = await repository.GetRegistrationAsync(command.UserId, ct);
        if (current?.Version != dialog.ExpectedProfileVersion || current is not null && current.BuildingId != complexId)
            return Close(dialog, "Профіль змінився. Почніть знову: /edit_profile або /delete_profile.");
        var now = clock.GetUtcNow();
        if (dialog.Action == ProfileAction.Delete)
        {
            if (command.Contact is not null || command.Text.Trim() != Confirmation) return DeletePrompt();
            var deleted = new ResidentRegistration(current?.Id ?? Guid.NewGuid().ToString("N"), command.UserId, complexId,
                RegistrationStage.Deleted, (current?.Version ?? 0) + 1, current?.CreatedAt ?? now, now,
                PersonalDataDeleted: true, PersonalDataDeletedAt: now, ApprovedAt: current?.ApprovedAt ?? current?.UpdatedAt ?? now);
            return Close(dialog, "Ваші ім’я, прізвище, телефон, email, будинок і номер квартири видалено з профілю. Заявки збережено. Доступ скасовано. Для подальшої роботи пройдіть реєстрацію заново: /start.") with
            { Profile = new(deleted, dialog.ExpectedProfileVersion) };
        }
        if (dialog.Action == ProfileAction.Account)
            return ContinueAccount(dialog, current, command, now);
        var outcome = RegistrationService.Advance(dialog.Draft ?? throw new InvalidOperationException("Missing profile draft."), command, now);
        if (outcome.Registration is not { } change) return outcome;
        if (change.Value.Stage == RegistrationStage.Pending)
        {
            var profile = change.Value with
            {
                Stage = RegistrationStage.Approved,
                ConsentAt = current?.ConsentAt,
                ConsentVersion = current?.ConsentVersion,
                Version = (dialog.ExpectedProfileVersion ?? 0) + 1,
                PersonalDataDeletedAt = current?.PersonalDataDeleted == true ? now : current?.PersonalDataDeletedAt
            };
            return Close(dialog, "Інформацію про вас оновлено. Доступ до системи збережено.") with { Profile = new(profile, dialog.ExpectedProfileVersion) };
        }
        return outcome with { Registration = null, ProfileDialog = new(dialog with { Draft = change.Value, Version = dialog.Version + 1 }, dialog.Version) };
    }
    private static CommandOutcome ContinueAccount(ResidentProfileDialog dialog, ResidentRegistration? current, CommandEnvelope command, DateTimeOffset now)
    {
        if (current is null || current.Stage != RegistrationStage.Approved || current.PersonalDataDeleted)
            return Close(dialog, "Дані профілю недоступні. Зверніться до адміністратора ОСББ.");
        var draft = dialog.Draft ?? throw new InvalidOperationException("Missing account draft.");
        if (draft.Stage == RegistrationStage.AwaitingBuilding)
        {
            var number = command.Text.Trim();
            if (number.StartsWith("Будинок ", StringComparison.OrdinalIgnoreCase)) number = number[8..].Trim();
            number = number.ToUpperInvariant();
            if (command.Contact is not null || !ValidBuilding(number)) return AccountPrompt(draft.Stage);
            draft = draft with { BuildingNumber = number };
            if (!TryApartment(draft.ApartmentNumber, out _))
                return AccountPrompt(RegistrationStage.AwaitingApartment) with
                { ProfileDialog = new(dialog with { Draft = draft with { Stage = RegistrationStage.AwaitingApartment }, Version = dialog.Version + 1 }, dialog.Version) };
        }
        else if (draft.Stage == RegistrationStage.AwaitingApartment)
        {
            if (command.Contact is not null || !TryApartment(command.Text, out var apartment)) return AccountPrompt(draft.Stage);
            draft = draft with { ApartmentNumber = apartment.ToString(CultureInfo.InvariantCulture) };
        }
        else throw new InvalidOperationException("Unknown account step.");
        var profile = current with
        {
            BuildingNumber = draft.BuildingNumber,
            ApartmentNumber = draft.ApartmentNumber,
            Version = current.Version + 1,
            UpdatedAt = now
        };
        if (!TryAccount(profile, out var account)) throw new InvalidOperationException("Invalid account address.");
        return Close(dialog, "Ваш особовий рахунок: № " + account) with { Profile = new(profile, current.Version) };
    }

    private static bool ValidBuilding(string? value) => ComplexLayout.BuildingNumbers.Contains(value, StringComparer.Ordinal);
    private static bool TryApartment(string? value, out int apartment) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out apartment) && apartment is >= 1 and <= 999;
    private static bool TryAccount(ResidentRegistration profile, out string account)
    {
        account = "";
        if (!ValidBuilding(profile.BuildingNumber) || !TryApartment(profile.ApartmentNumber, out var apartment)) return false;
        account = "000219" + profile.BuildingNumber!.ToLowerInvariant() + apartment.ToString("D3", CultureInfo.InvariantCulture);
        return true;
    }
    private static CommandOutcome AccountPrompt(RegistrationStage stage) => stage == RegistrationStage.AwaitingBuilding
        ? new("Для визначення особового рахунку оберіть будинок проживання: 4Б, 4В, 4Г або 4Д. Скасувати: /cancel.", Keyboard: ReplyKeyboard.RegistrationBuilding)
        : new("Введіть номер квартири від 1 до 999. Скасувати: /cancel.", Keyboard: ReplyKeyboard.Remove);

    private static CommandOutcome EditPrompt(RegistrationStage stage) => stage == RegistrationStage.AwaitingPhone
        ? new("Оновлення профілю. Попередні дані збережуться до завершення всіх кроків. " +
            "Доступ залишиться чинним, повторне схвалення не потрібне.\n\n" +
            "Натисніть «Поділитися номером телефону», потім введіть ім’я, прізвище, будинок, номер квартири та за бажанням email. Скасувати: /cancel.",
            Keyboard: ReplyKeyboard.RequestContact)
        : RegistrationService.Prompt(stage);
    private void Validate(ResidentProfileDialog? dialog, long userId)
    {
        if (dialog is not null && (dialog.UserId != userId || dialog.ComplexId != complexId))
            throw new InvalidOperationException("Profile dialog identity mismatch.");
    }
    private static CommandOutcome Close(ResidentProfileDialog dialog, string text) => new(text, Keyboard: ReplyKeyboard.Remove,
        ProfileDialog: new(dialog with { Draft = null, Active = false, Version = dialog.Version + 1 }, dialog.Version));
    private static CommandOutcome DeletePrompt() => new("Видалити ім’я, прізвище, телефон, email, будинок і квартиру з профілю? Заявки залишаться, " +
        "Доступ буде скасовано - для подальшої роботи потрібно зареєструватися заново. Автор заявок буде позначений як «Мешканець видалив свої дані».\n\nДля підтвердження натисніть «Видалити мої дані». Скасувати: /cancel.",
        Keyboard: ReplyKeyboard.ProfileDeleteConfirmation);
}
