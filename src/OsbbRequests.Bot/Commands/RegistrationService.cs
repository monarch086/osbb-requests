using OsbbRequests.Shared.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;

namespace OsbbRequests.Bot.Commands;

public sealed partial class RegistrationService(ICommandRepository repository, string buildingId, TimeProvider clock)
{
    public async Task<CommandOutcome> StartAsync(long userId, CancellationToken cancellationToken, bool restart = false)
    {
        if (await repository.IsResidentAsync(userId, buildingId, cancellationToken))
            return new CommandOutcome("Доступ до будинку надано.\n\n" + CommandHandler.Help, Keyboard: ReplyKeyboard.Remove);
        var current = await repository.GetRegistrationAsync(userId, cancellationToken);
        if (!restart && current is { Stage: RegistrationStage.Rejected or RegistrationStage.Approved })
            return Status(current);
        if (current is { Stage: RegistrationStage.Pending })
            return Status(current);
        if (current is not null && IsDraft(current.Stage))
            return Prompt(current.Stage);
        var now = clock.GetUtcNow();
        var registration = new ResidentRegistration(Guid.NewGuid().ToString("N"), userId, buildingId,
            RegistrationStage.AwaitingPhone, (current?.Version ?? 0) + 1, now, now);
        return Prompt(registration.Stage) with { Registration = new RegistrationChange(registration, current?.Version) };
    }

    public async Task<CommandOutcome> CancelAsync(long userId, CancellationToken cancellationToken)
    {
        var current = await repository.GetRegistrationAsync(userId, cancellationToken);
        if (current is null || !IsDraft(current.Stage))
            return new CommandOutcome("Немає незавершеної реєстрації. Перевірити доступ: /start.", Keyboard: ReplyKeyboard.Remove);
        var cancelled = current with
        {
            Stage = RegistrationStage.Cancelled,
            Phone = null,
            FirstName = null,
            LastName = null,
            Version = current.Version + 1,
            UpdatedAt = clock.GetUtcNow()
        };
        return new CommandOutcome("Реєстрацію скасовано. Почати знову: /register.",
            Registration: new RegistrationChange(cancelled, current.Version), Keyboard: ReplyKeyboard.Remove);
    }

    public async Task<CommandOutcome> StatusAsync(long userId, CancellationToken cancellationToken)
    {
        var current = await repository.GetRegistrationAsync(userId, cancellationToken);
        return current is null ? new CommandOutcome("Для доступу до заявок зареєструйтеся: /register.") : Status(current);
    }

    public async Task<CommandOutcome> ContinueAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var current = await repository.GetRegistrationAsync(envelope.UserId, cancellationToken);
        if (current is null)
            return new CommandOutcome("Спочатку почніть реєстрацію: /register.");
        if (!IsDraft(current.Stage))
            return Status(current);
        if (current.BuildingId != buildingId)
            throw new InvalidOperationException("Registration building mismatch.");

        ResidentRegistration next;
        switch (current.Stage)
        {
            case RegistrationStage.AwaitingPhone:
                if (envelope.Contact is not { UserId: { } contactOwner, PhoneNumber: { } phone } ||
                    contactOwner != envelope.UserId || !TryPhone(phone, out var normalized))
                    return new CommandOutcome("Надішліть власний номер кнопкою «Поділитися номером телефону». " +
                        "Чужий контакт або номер, введений текстом, не підходить.", Keyboard: ReplyKeyboard.RequestContact);
                next = current with { Phone = normalized, Stage = RegistrationStage.AwaitingFirstName };
                break;
            case RegistrationStage.AwaitingFirstName:
                if (envelope.Contact is not null || !ValidName(envelope.Text))
                    return new CommandOutcome("Введіть своє ім’я текстом: від 1 до 80 символів, без цифр.", Keyboard: ReplyKeyboard.Remove);
                next = current with { FirstName = envelope.Text.Trim(), Stage = RegistrationStage.AwaitingLastName };
                break;
            default:
                if (envelope.Contact is not null || !ValidName(envelope.Text))
                    return new CommandOutcome("Введіть своє прізвище текстом: від 1 до 80 символів, без цифр.", Keyboard: ReplyKeyboard.Remove);
                next = current with { LastName = envelope.Text.Trim(), Stage = RegistrationStage.Pending };
                break;
        }
        next = next with { Version = current.Version + 1, UpdatedAt = clock.GetUtcNow() };
        var outcome = next.Stage == RegistrationStage.Pending
            ? new CommandOutcome($"Дякуємо, {next.FirstName} {next.LastName}!\n" +
                $"Телефон: {next.Phone}\n\nЗапит на доступ передано адміністратору. " +
                "Після перевірки даних адміністратор надасть доступ. Перевірити стан: /start.", Keyboard: ReplyKeyboard.Remove)
            : Prompt(next.Stage);
        return outcome with { Registration = new RegistrationChange(next, current.Version) };
    }

    public static bool IsDraft(RegistrationStage stage) => stage is RegistrationStage.AwaitingPhone or
        RegistrationStage.AwaitingFirstName or RegistrationStage.AwaitingLastName;

    private static CommandOutcome Prompt(RegistrationStage stage) => stage switch
    {
        RegistrationStage.AwaitingPhone => new CommandOutcome("Для реєстрації потрібні ваш мобільний номер, ім’я та прізвище. " +
            "Їх побачить адміністратор ОСББ для перевірки та надання доступу.\n\n" +
            "Натисніть «Поділитися номером телефону», потім введіть ім’я та прізвище. Скасувати: /cancel.",
            Keyboard: ReplyKeyboard.RequestContact),
        RegistrationStage.AwaitingFirstName => new CommandOutcome("Дякуємо! Тепер введіть своє ім’я.", Keyboard: ReplyKeyboard.Remove),
        _ => new CommandOutcome("Введіть своє прізвище.", Keyboard: ReplyKeyboard.Remove)
    };

    private static CommandOutcome Status(ResidentRegistration registration) => registration.Stage switch
    {
        RegistrationStage.Pending => new CommandOutcome("Ваш запит на доступ очікує перевірки адміністратором. Перевірити стан: /start.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Rejected => new CommandOutcome("Адміністратор відхилив запит на доступ. " +
            (registration.DecisionReason is { Length: > 0 } reason ? "Причина: " + reason + "\n" : "") +
            "Уточніть дані та почніть реєстрацію знову: /register.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Approved => new CommandOutcome("Доступ до заявок зараз неактивний. Зверніться до адміністратора ОСББ.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Cancelled => new CommandOutcome("Реєстрацію скасовано. Почати знову: /register.", Keyboard: ReplyKeyboard.Remove),
        _ => Prompt(registration.Stage)
    };

    private static bool TryPhone(string value, out string normalized)
    {
        normalized = string.Concat(value.Where(c => c is not (' ' or '(' or ')' or '-')));
        if (!normalized.StartsWith('+'))
            normalized = "+" + normalized;
        return PhonePattern().IsMatch(normalized);
    }

    private static bool ValidName(string value)
    {
        var name = value.Trim();
        return name.Length is >= 1 and <= 80 && name.Any(char.IsLetter) &&
            name.All(c => char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark ||
                c is ' ' or '-' or '\'' or '’' or 'ʼ');
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
