using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Domain;
using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using OsbbRequests.Bot.Models;
using OsbbRequests.Bot.Persistence;

namespace OsbbRequests.Bot.Commands;

public sealed partial class RegistrationService(ICommandRepository repository, string buildingId, TimeProvider clock, ResidentApprovalMode approvalMode = ResidentApprovalMode.Automatic)
{
    public const string SkipEmail = "Пропустити";
    public const string ConsentButton = "Згоден на обробку персональних даних";
    public const string ConsentVersion = "3";
    public const string ConsentText = "Обробка персональних даних\n\n" +
        "Володілець даних: ОСББ \"Зелений острів 2\". " +
        "Для реєстрації та роботи бота обробляються ваші ім’я, прізвище, номер телефону, будинок, квартира, необов’язкова email-адреса, " +
        "ідентифікатори Telegram та відомості, які ви надаєте у заявках.\n\n" +
        "Мета: надання доступу до бота, опрацювання звернень мешканців і надсилання пов’язаних повідомлень ОСББ. " +
        "Доступ до профілю мають уповноважені адміністратори ОСББ. Зміст заявок із видимістю для мешканців комплексу доступний іншим мешканцям. " +
        "Для роботи сервісу дані обробляються також постачальниками Telegram та AWS, зокрема за межами України.\n\n" +
        "Ви маєте право знати про обробку та отримувати доступ до своїх даних, вимагати їх виправлення чи видалення, " +
        "заперечувати проти обробки, відкликати згоду та звертатися зі скаргою у випадках, передбачених законом. " +
        "Повний перелік прав: https://zakon.rada.gov.ua/laws/show/2297-17#Text (стаття 8). " +
        "Змінити або видалити дані профілю можна командами /edit_profile і /delete_profile. " +
        "Письмові звернення щодо даних можна надсилати до ОСББ за адресою: Україна, м. Київ, вул. Авіаконструктора Ігоря Сікорського, буд. 4-Б. " +
        "Після видалення профілю доступ до бота припиняється; " +
        "заявки та деякі службові записи зберігаються відповідно до призначення системи та встановлених строків.\n\n" +
        "Натискаючи кнопку, ви надаєте згоду на описану обробку. Без згоди реєстрація неможлива. Скасувати: /cancel.";
    public async Task<CommandOutcome> StartAsync(long userId, CancellationToken cancellationToken)
    {
        if (await repository.IsResidentAsync(userId, buildingId, cancellationToken))
            return new CommandOutcome("Доступ до ЖК «Зелений острів 2» надано.\n\n" +
                "У боті можна:\n" +
                "• створити заявку та відстежувати її статус;\n" +
                "• переглянути поточні заявки комплексу;\n" +
                "• отримувати сповіщення про зміни своєї заявки;\n" +
                "• змінити або видалити свої персональні дані.", Keyboard: ReplyKeyboard.Remove);
        var current = await repository.GetRegistrationAsync(userId, cancellationToken);
        if (current is { Stage: RegistrationStage.Pending })
            return Status(current);
        if (current is not null && IsDraft(current.Stage))
        {
            if (current.ConsentAt is null && (current.Stage != RegistrationStage.AwaitingConsent || current.ConsentVersion != ConsentVersion))
                return RequireConsent(current, clock.GetUtcNow());
            return Prompt(current.Stage, approvalMode);
        }
        var now = clock.GetUtcNow();
        var registration = new ResidentRegistration(Guid.NewGuid().ToString("N"), userId, buildingId,
            RegistrationStage.AwaitingConsent, (current?.Version ?? 0) + 1, now, now,
            PersonalDataDeletedAt: current?.PersonalDataDeleted == true ? now : current?.PersonalDataDeletedAt, ConsentVersion: ConsentVersion);
        var prompt = Prompt(registration.Stage, approvalMode);
        if (current is { Stage: RegistrationStage.Rejected, DecisionReason: { Length: > 0 } reason })
            prompt = prompt with { Reply = "Попередню реєстрацію відхилено. Причина: " + reason + "\n\n" + prompt.Reply };
        return prompt with { Registration = new RegistrationChange(registration, current?.Version) };
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
            Email = null,
            Version = current.Version + 1,
            UpdatedAt = clock.GetUtcNow()
        };
        return new CommandOutcome("Реєстрацію скасовано. Почати знову: /start.",
            Registration: new RegistrationChange(cancelled, current.Version), Keyboard: ReplyKeyboard.Remove);
    }

    public async Task<CommandOutcome> StatusAsync(long userId, CancellationToken cancellationToken)
    {
        var current = await repository.GetRegistrationAsync(userId, cancellationToken);
        if (current is not null && IsDraft(current.Stage) && current.ConsentAt is null && (current.Stage != RegistrationStage.AwaitingConsent || current.ConsentVersion != ConsentVersion))
            return RequireConsent(current, clock.GetUtcNow());
        return current is null ? new CommandOutcome("Для доступу до заявок зареєструйтеся: /start.") : Status(current);
    }

    public async Task<CommandOutcome> ContinueAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var current = await repository.GetRegistrationAsync(envelope.UserId, cancellationToken);
        if (current is null)
            return new CommandOutcome("Спочатку почніть реєстрацію: /start.");
        if (!IsDraft(current.Stage))
            return Status(current);
        if (current.BuildingId != buildingId)
            throw new InvalidOperationException("Registration building mismatch.");

        if (current.ConsentAt is null && (current.Stage != RegistrationStage.AwaitingConsent || current.ConsentVersion != ConsentVersion))
            return RequireConsent(current, clock.GetUtcNow());
        var outcome = Advance(current, envelope, clock.GetUtcNow());
        if (current.Stage == RegistrationStage.AwaitingConsent && outcome.Registration is { Value.Stage: RegistrationStage.AwaitingPhone } consentChange)
            outcome = Prompt(RegistrationStage.AwaitingPhone, approvalMode) with { Registration = consentChange };
        if (current.Stage == RegistrationStage.AwaitingEmail && outcome.Registration is { Value.Stage: RegistrationStage.Pending } submitted)
        {
            var next = submitted.Value with { Stage = RegistrationStage.AwaitingResidentType };
            return Prompt(next.Stage) with { Registration = submitted with { Value = next } };
        }
        if (approvalMode != ResidentApprovalMode.Automatic || outcome.Registration?.Value.Stage != RegistrationStage.Pending ||
            outcome.Registration.Value.ResidentType == ResidentType.Owner)
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
            case RegistrationStage.AwaitingConsent:
                if (envelope.Contact is not null || envelope.Text.Trim() != ConsentButton)
                    return Prompt(RegistrationStage.AwaitingConsent);
                next = current with
                {
                    ConsentAt = now,
                    ConsentVersion = ConsentVersion,
                    Stage = current.ResumeAfterConsent ?? RegistrationStage.AwaitingPhone,
                    ResumeAfterConsent = null
                };
                break;
            case RegistrationStage.AwaitingPhone:
                if (envelope.Contact is not { UserId: { } contactOwner, PhoneNumber: { } phone } ||
                    contactOwner != envelope.UserId || !TryPhone(phone, out var normalized))
                    return new CommandOutcome("Надішліть власний номер кнопкою «Поділитися номером телефону». " +
                        "Чужий контакт або номер, введений текстом, не підходить.", Keyboard: ReplyKeyboard.RequestContact);
                next = current with { Phone = normalized, Stage = RegistrationStage.AwaitingFirstName };
                break;
            case RegistrationStage.AwaitingFirstName:
                if (envelope.Contact is not null || !ValidName(envelope.Text))
                    return new CommandOutcome("Введіть своє ім’я (без прізвища) текстом: від 1 до 80 символів, без цифр.", Keyboard: ReplyKeyboard.Remove);
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
                next = current with { ApartmentNumber = apartment.ToString(CultureInfo.InvariantCulture), Stage = RegistrationStage.AwaitingEmail };
                break;
            case RegistrationStage.AwaitingResidentType:
                if (envelope.Contact is not null) return Prompt(current.Stage);
                var residentType = envelope.Text.Trim().ToLowerInvariant() switch
                {
                    "власник" => ResidentType.Owner,
                    "член родини" => ResidentType.FamilyMember,
                    "орендар" => ResidentType.Tenant,
                    _ => (ResidentType?)null
                };
                if (residentType is null) return Prompt(current.Stage);
                next = current with { ResidentType = residentType.Value, Stage = RegistrationStage.Pending };
                break;
            case RegistrationStage.AwaitingEmail:
                var email = envelope.Text.Trim();
                if (envelope.Contact is not null || (email != SkipEmail && !ValidEmail(email)))
                    return new CommandOutcome("Введіть коректну email-адресу або натисніть «Пропустити».", Keyboard: ReplyKeyboard.OptionalEmail);
                next = current with { Email = email == SkipEmail ? null : email, Stage = RegistrationStage.Pending };
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

    private static CommandOutcome RequireConsent(ResidentRegistration current, DateTimeOffset now)
    {
        var next = current with
        {
            Stage = RegistrationStage.AwaitingConsent,
            ResumeAfterConsent = current.Stage == RegistrationStage.AwaitingConsent ? current.ResumeAfterConsent : current.Stage,
            ConsentVersion = ConsentVersion,
            Version = current.Version + 1,
            UpdatedAt = now
        };
        return Prompt(next.Stage) with { Registration = new RegistrationChange(next, current.Version) };
    }

    public static bool IsDraft(RegistrationStage stage) => stage is RegistrationStage.AwaitingConsent or RegistrationStage.AwaitingPhone or
        RegistrationStage.AwaitingFirstName or RegistrationStage.AwaitingLastName or RegistrationStage.AwaitingBuilding or RegistrationStage.AwaitingApartment or RegistrationStage.AwaitingEmail or RegistrationStage.AwaitingResidentType;

    public static CommandOutcome Prompt(RegistrationStage stage, ResidentApprovalMode approvalMode = ResidentApprovalMode.Manual) => stage switch
    {
        RegistrationStage.AwaitingConsent => new CommandOutcome(ConsentText, Keyboard: ReplyKeyboard.PersonalDataConsent),
        RegistrationStage.AwaitingPhone => new CommandOutcome("Для реєстрації в системі потрібні ваш мобільний номер, ім’я, прізвище, будинок і номер квартири. " +
            (approvalMode == ResidentApprovalMode.Automatic
                ? "Після заповнення даних власник очікує підтвердження адміністратора; членам родини та орендарям доступ надається автоматично. Дані профілю доступні адміністраторам комплексу.\n\n"
                : "Їх побачить адміністратор ОСББ для перевірки та надання доступу.\n\n") +
            "Натисніть «Поділитися номером телефону», потім введіть ім’я, прізвище, оберіть будинок, введіть номер квартири та за бажанням email. Скасувати: /cancel.",
            Keyboard: ReplyKeyboard.RequestContact),
        RegistrationStage.AwaitingFirstName => new CommandOutcome("Дякуємо! Тепер введіть своє ім’я.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.AwaitingLastName => new CommandOutcome("Введіть своє прізвище.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.AwaitingBuilding => new CommandOutcome("У якому будинку ви проживаєте? Оберіть 4Б, 4В, 4Г або 4Д.", Keyboard: ReplyKeyboard.RegistrationBuilding),
        RegistrationStage.AwaitingApartment => new CommandOutcome("Введіть номер своєї квартири, наприклад 125.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.AwaitingResidentType => new CommandOutcome("Оберіть тип мешканця: Власник, Член родини або Орендар. Реєстрацію власника має підтвердити адміністратор. Скасувати: /cancel.", Keyboard: ReplyKeyboard.ResidentType),
        RegistrationStage.AwaitingEmail => new CommandOutcome("Введіть вашу email-адресу або натисніть «Пропустити». Email необов’язковий.", Keyboard: ReplyKeyboard.OptionalEmail),
        _ => throw new InvalidOperationException("Unknown registration step.")
    };

    private static CommandOutcome Status(ResidentRegistration registration) => registration.Stage switch
    {
        RegistrationStage.Pending => new CommandOutcome("Ваш запит на доступ очікує перевірки адміністратором. Перевірити стан: /start.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Rejected => new CommandOutcome("Адміністратор відхилив запит на доступ. " +
            (registration.DecisionReason is { Length: > 0 } reason ? "Причина: " + reason + "\n" : "") +
            "Уточніть дані та почніть реєстрацію знову: /start.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Approved => new CommandOutcome("Доступ до заявок зараз неактивний. Зверніться до адміністратора ОСББ.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Deleted => new CommandOutcome("Ви видалили свої дані. Для доступу до заявок пройдіть реєстрацію заново: /start.", Keyboard: ReplyKeyboard.Remove),
        RegistrationStage.Cancelled => new CommandOutcome("Реєстрацію скасовано. Почати знову: /start.", Keyboard: ReplyKeyboard.Remove),
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

    private static bool ValidEmail(string email) => email.Length is > 0 and <= 254 &&
        !email.Any(char.IsWhiteSpace) && MailAddress.TryCreate(email, out var address) &&
        string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase) && address.Host.Contains('.', StringComparison.Ordinal);

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
