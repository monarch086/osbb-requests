using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;
using System.Globalization;
using System.Text.RegularExpressions;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;

namespace OsbbRequests.Bot.Commands;

public sealed partial class RegistrationService(ICommandRepository repository, string buildingId, TimeProvider clock, ResidentApprovalMode approvalMode = ResidentApprovalMode.Automatic)
{
    public async Task<CommandOutcome> StartAsync(long userId, CancellationToken cancellationToken, bool restart = false)
    {
        if (await repository.IsResidentAsync(userId, buildingId, cancellationToken))
            return new CommandOutcome("Доступ до ЖК «Зелений острів 2» надано.\n\n" + CommandHandler.Help, Keyboard: ReplyKeyboard.Remove);
        var current = await repository.GetRegistrationAsync(userId, cancellationToken);
        if (!restart && current is { Stage: RegistrationStage.Rejected or RegistrationStage.Approved })
            return Status(current);
        if (current is { Stage: RegistrationStage.Pending })
            return Status(current);
        if (current is not null && IsDraft(current.Stage))
            return Prompt(current.Stage, approvalMode);
        var now = clock.GetUtcNow();
        var registration = new ResidentRegistration(Guid.NewGuid().ToString("N"), userId, buildingId,
            RegistrationStage.AwaitingPhone, (current?.Version ?? 0) + 1, now, now,
            PersonalDataDeletedAt: current?.PersonalDataDeleted == true ? now : current?.PersonalDataDeletedAt);
        return Prompt(registration.Stage, approvalMode) with { Registration = new RegistrationChange(registration, current?.Version) };
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
            BuildingNumber = null,
            ApartmentNumber = null,
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

        var outcome = Advance(current, envelope, clock.GetUtcNow());
        if (approvalMode != ResidentApprovalMode.Automatic || outcome.Registration?.Value.Stage != RegistrationStage.Pending)
            return outcome;
        var change = outcome.Registration;
        var approved = change.Value with { Stage = RegistrationStage.Approved, ApprovedAt = change.Value.UpdatedAt };
        return new CommandOutcome("Реєстрацію завершено! Вам автоматично надано доступ до бота ЖК «Зелений острів 2».\n\n" +
            "Створити заявку: /new\nВаші заявки: /my\nЗаявки комплексу: /complex",
            Registration: change with { Value = approved, AutomaticallyApproved = true }, Keyboard: ReplyKeyboard.Remove);
    }

    public static CommandOutcome Advance(ResidentRegistration current, CommandEnvelope envelope, DateTimeOffset now)
    {
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
            case RegistrationStage.AwaitingLastName:
                if (envelope.Contact is not null || !ValidName(envelope.Text))
                    return new CommandOutcome("Введіть своє прізвище текстом: від 1 до 80 символів, без цифр.", Keyboard: ReplyKeyboard.Remove);
                next = current with { LastName = envelope.Text.Trim(), Stage = RegistrationStage.AwaitingBuilding };
                break;
            case RegistrationStage.AwaitingBuilding:
                var building = envelope.Text.Trim();
                if (building.StartsWith("Будинок ", StringComparison.OrdinalIgnoreCase)) building = building[8..].Trim();
                building = building.ToUpperInvariant();
                if (envelope.Contact is not null || !ComplexLayout.BuildingNumbers.Contains(building, StringComparer.Ordinal))
                    return Prompt(RegistrationStage.AwaitingBuilding);
                next = current with { BuildingNumber = building, Stage = RegistrationStage.AwaitingApartment };
                break;
            case RegistrationStage.AwaitingApartment:
                if (envelope.Contact is not null || !int.TryParse(envelope.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var apartment) || apartment <= 0)
                    return new CommandOutcome("Введіть номер квартири цілим додатним числом, наприклад 125.", Keyboard: ReplyKeyboard.Remove);
                next = current with { ApartmentNumber = apartment.ToString(CultureInfo.InvariantCulture), Stage = RegistrationStage.Pending };
                break;
            default:
                throw new InvalidOperationException("Unknown registration step.");
        }
        next = next with { Version = current.Version + 1, UpdatedAt = now };
        var outcome = next.Stage == RegistrationStage.Pending
            ? new CommandOutcome($"Дякуємо, {next.FirstName} {next.LastName}!\n" +
                $"Телефон: {next.Phone}\nБудинок: {next.BuildingNumber}, квартира: {next.ApartmentNumber}\n\nЗапит на доступ передано адміністратору. " +
                "Після перевірки даних адміністратор надасть доступ. Перевірити стан: /start.", Keyboard: ReplyKeyboard.Remove)
            : Prompt(next.Stage);
        return outcome with { Registration = new RegistrationChange(next, current.Version) };
    }

    public static bool IsDraft(RegistrationStage stage) => stage is RegistrationStage.AwaitingPhone or
        RegistrationStage.AwaitingFirstName or RegistrationStage.AwaitingLastName or RegistrationStage.AwaitingBuilding or RegistrationStage.AwaitingApartment;

    public static CommandOutcome Prompt(RegistrationStage stage, ResidentApprovalMode approvalMode = ResidentApprovalMode.Manual) => stage switch
    {
        RegistrationStage.AwaitingPhone => new CommandOutcome("Для реєстрації в системі потрібні ваш мобільний номер, ім’я, прізвище, будинок і номер квартири. " +
            (approvalMode == ResidentApprovalMode.Automatic
                ? "Після заповнення всіх даних доступ буде надано автоматично. Дані профілю доступні адміністраторам комплексу.\n\n"
                : "Їх побачить адміністратор ОСББ для перевірки та надання доступу.\n\n") +
            "Натисніть «Поділитися номером телефону», потім введіть ім’я, прізвище, оберіть будинок і введіть номер квартири. Скасувати: /cancel.",
            Keyboard: ReplyKeyboard.RequestContact),
        RegistrationStage.AwaitingFirstName => new CommandOutcome("Дякуємо! Тепер введіть своє ім’я.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.AwaitingLastName => new CommandOutcome("Введіть своє прізвище.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.AwaitingBuilding => new CommandOutcome("У якому будинку ви проживаєте? Оберіть 4Б, 4В, 4Г або 4Д.", Keyboard: ReplyKeyboard.RegistrationBuilding),
        RegistrationStage.AwaitingApartment => new CommandOutcome("Введіть номер своєї квартири, наприклад 125.", Keyboard: ReplyKeyboard.Remove),
        _ => throw new InvalidOperationException("Unknown registration step.")
    };

    private static CommandOutcome Status(ResidentRegistration registration) => registration.Stage switch
    {
        RegistrationStage.Pending => new CommandOutcome("Ваш запит на доступ очікує перевірки адміністратором. Перевірити стан: /start.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Rejected => new CommandOutcome("Адміністратор відхилив запит на доступ. " +
            (registration.DecisionReason is { Length: > 0 } reason ? "Причина: " + reason + "\n" : "") +
            "Уточніть дані та почніть реєстрацію знову: /register.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Approved => new CommandOutcome("Доступ до заявок зараз неактивний. Зверніться до адміністратора ОСББ.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Deleted => new CommandOutcome("Ви видалили свої дані. Для доступу до заявок пройдіть реєстрацію заново: /start або /register.", Keyboard: ReplyKeyboard.Remove),
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
