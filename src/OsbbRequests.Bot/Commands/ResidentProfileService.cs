using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;
using OsbbRequests.Shared.Models;

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
        if (previous is { Active: true, Action: ProfileAction.Edit, Draft: { } draft } && action == ProfileAction.Edit)
            return EditPrompt(draft.Stage);
        var now = clock.GetUtcNow();
        var value = new ResidentRegistration(current?.Id ?? Guid.NewGuid().ToString("N"), userId, complexId,
            RegistrationStage.AwaitingPhone, 1, current?.CreatedAt ?? now, now, PersonalDataDeletedAt: current?.PersonalDataDeletedAt, ApprovedAt: current?.ApprovedAt ?? current?.UpdatedAt ?? now);
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
                RegistrationStage.Approved, (current?.Version ?? 0) + 1, current?.CreatedAt ?? now, now,
                PersonalDataDeleted: true, PersonalDataDeletedAt: now, ApprovedAt: current?.ApprovedAt ?? current?.UpdatedAt ?? now);
            return Close(dialog, "Ваші ім’я, прізвище, телефон, будинок і номер квартири видалено з профілю. Заявки та доступ до них збережено.") with
            { Profile = new(deleted, dialog.ExpectedProfileVersion) };
        }
        var outcome = RegistrationService.Advance(dialog.Draft ?? throw new InvalidOperationException("Missing profile draft."), command, now);
        if (outcome.Registration is not { } change) return outcome;
        if (change.Value.Stage == RegistrationStage.Pending)
        {
            var profile = change.Value with
            {
                Stage = RegistrationStage.Approved,
                Version = (dialog.ExpectedProfileVersion ?? 0) + 1,
                PersonalDataDeletedAt = current?.PersonalDataDeleted == true ? now : current?.PersonalDataDeletedAt
            };
            return Close(dialog, "Інформацію про вас оновлено. Доступ до системи збережено.") with { Profile = new(profile, dialog.ExpectedProfileVersion) };
        }
        return outcome with { Registration = null, ProfileDialog = new(dialog with { Draft = change.Value, Version = dialog.Version + 1 }, dialog.Version) };
    }
    private static CommandOutcome EditPrompt(RegistrationStage stage) => stage == RegistrationStage.AwaitingPhone
        ? new("Оновлення профілю. Попередні дані збережуться до завершення всіх кроків. " +
            "Доступ залишиться чинним, повторне схвалення не потрібне.\n\n" +
            "Натисніть «Поділитися номером телефону», потім введіть ім’я, прізвище, будинок і номер квартири. Скасувати: /cancel.",
            Keyboard: ReplyKeyboard.RequestContact)
        : RegistrationService.Prompt(stage);
    private void Validate(ResidentProfileDialog? dialog, long userId)
    {
        if (dialog is not null && (dialog.UserId != userId || dialog.ComplexId != complexId))
            throw new InvalidOperationException("Profile dialog identity mismatch.");
    }
    private static CommandOutcome Close(ResidentProfileDialog dialog, string text) => new(text, Keyboard: ReplyKeyboard.Remove,
        ProfileDialog: new(dialog with { Draft = null, Active = false, Version = dialog.Version + 1 }, dialog.Version));
    private static CommandOutcome DeletePrompt() => new("Видалити ім’я, прізвище, телефон, будинок і квартиру з профілю? Заявки залишаться, " +
        "а їхній автор буде позначений як «Мешканець видалив свої дані».\n\nДля підтвердження натисніть «Видалити мої дані». Скасувати: /cancel.",
        Keyboard: ReplyKeyboard.ProfileDeleteConfirmation);
}
