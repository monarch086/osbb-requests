using OsbbRequests.Bot.Models;
using OsbbRequests.Shared.Models;
using OsbbRequests.Shared.Residents;

namespace OsbbRequests.Bot.Commands;

public sealed partial class ResidentProfileService
{
    public const string AddApartment = "Додати квартиру";

    private async Task<OwnerApartments?> ApartmentsAsync(ResidentRegistration current, CancellationToken ct)
    {
        var apartments = await repository.GetOwnerApartmentsAsync(current.UserId, ct);
        return apartments?.RegistrationId == current.Id ? apartments : null;
    }

    private async Task<List<(string Address, string Number)>> AccountsAsync(ResidentRegistration current, CancellationToken ct)
    {
        var accounts = new List<(string Address, string Number)>();
        if (TryAccount(current, out var primary))
            accounts.Add(($"Будинок {current.BuildingNumber}, кв. {current.ApartmentNumber}", primary));
        var stored = await ApartmentsAsync(current, ct);
        foreach (var apartment in stored?.Items ?? [])
        {
            if (apartment.Status != ApartmentReviewStatus.Approved) continue;
            var number = FormattableString.Invariant($"000219{apartment.BuildingNumber.ToLowerInvariant()}{apartment.ApartmentNumber:D3}");
            if (accounts.All(account => account.Number != number)) accounts.Add((apartment.Address, number));
        }
        return accounts;
    }

    private static CommandOutcome ApartmentMenu(ResidentRegistration profile, OwnerApartments? stored)
    {
        var text = ValidBuilding(profile.BuildingNumber) && TryApartment(profile.ApartmentNumber, out var primary)
            ? $"Основна адреса: будинок {profile.BuildingNumber}, кв. {primary}.\n\nДодаткові квартири"
            : "Додаткові квартири";
        foreach (var apartment in stored?.Items ?? [])
        {
            var status = apartment.Status switch
            {
                ApartmentReviewStatus.Approved => "Підтверджено",
                ApartmentReviewStatus.Rejected => "Відхилено",
                _ => "Очікує підтвердження"
            };
            text += $"\n{apartment.Address} — {status}";
        }
        return new(text + "\n\nМожете додати квартиру для перевірки адміністратором.", Keyboard: ReplyKeyboard.OwnerApartments);
    }

    private async Task<CommandOutcome> ContinueApartmentsAsync(ResidentProfileDialog dialog, ResidentRegistration? current,
        CommandEnvelope command, DateTimeOffset now, CancellationToken ct)
    {
        if (current is null || !OwnerApartments.CanManage(current))
            return Close(dialog, "Додавати квартири можуть лише підтверджені власники.");

        if (!dialog.AddingApartment)
        {
            var stored = await ApartmentsAsync(current, ct);
            if (command.Contact is not null || command.Text.Trim() != AddApartment) return ApartmentMenu(current, stored);
            if (stored?.Items.Count >= OwnerApartments.MaxAdditional)
                return Close(dialog, $"До профілю можна додати до {OwnerApartments.MaxAdditional} квартир.");
            return new("Оберіть будинок додаткової квартири.", Keyboard: ReplyKeyboard.RegistrationBuilding,
                ProfileDialog: new(dialog with { AddingApartment = true, Version = dialog.Version + 1 }, dialog.Version));
        }
        if (dialog.Draft is null)
        {
            var building = command.Text.Trim();
            if (building.StartsWith("Будинок ", StringComparison.OrdinalIgnoreCase)) building = building[8..].Trim();
            building = building.ToUpperInvariant();
            if (command.Contact is not null || !ValidBuilding(building))
                return new("Оберіть будинок додаткової квартири.", Keyboard: ReplyKeyboard.RegistrationBuilding);
            return new("Введіть номер квартири від 1 до 999.", Keyboard: ReplyKeyboard.Cancel,
                ProfileDialog: new(dialog with { Draft = current with { BuildingNumber = building }, Version = dialog.Version + 1 }, dialog.Version));
        }
        if (command.Contact is not null || !TryApartment(command.Text, out var number))
            return new("Введіть номер квартири від 1 до 999.", Keyboard: ReplyKeyboard.Cancel);

        var existing = await repository.GetOwnerApartmentsAsync(current.UserId, ct);
        var items = existing?.RegistrationId == current.Id ? existing.Items : [];
        var address = dialog.Draft.BuildingNumber!;
        if ((current.BuildingNumber == address && TryApartment(current.ApartmentNumber, out var primary) && number == primary) ||
            items.Any(item => item.BuildingNumber == address && item.ApartmentNumber == number))
            return Close(dialog, "Ця квартира вже є у вашому профілі. Відкрийте «Мої квартири», щоб перевірити її статус.");
        if (items.Count >= OwnerApartments.MaxAdditional)
            return Close(dialog, $"До профілю можна додати до {OwnerApartments.MaxAdditional} квартир.");
        var apartment = new OwnerApartment(Guid.NewGuid().ToString("N"), address, number, now);
        var next = new OwnerApartments(complexId, current.UserId, current.Id, (existing?.Version ?? 0) + 1, [.. items, apartment]);
        return Close(dialog, $"{apartment.Address}. Запит передано адміністратору. Результат надійде в Telegram.") with
        { Apartment = new(next, existing?.Version, current.Version, apartment.Id) };
    }
}
