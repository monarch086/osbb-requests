using Microsoft.EntityFrameworkCore;
using OsbbRequests.CommandProcessor.Commands;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Telegram;
using OsbbRequests.Domain;
using OsbbRequests.Domain.Models;
using OsbbRequests.Postgres.Bot;
using OsbbRequests.Shared.Residents;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    private const string CommandComplex = "osbb-requests-dev";
    private static readonly DateTimeOffset RegistrationTime = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task CommandBotUsesExistingPostgresTablesAndDoesNotReplayCommittedWrites()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var repository = await PostgresCommandRepository.ConnectAsync(database, CommandComplex, "testbot", default);
        await RegisterCommandResidentAsync(repository, 42);
        Assert.True(await repository.IsResidentAsync(42, CommandComplex, default));
        Assert.True(await repository.ResidentPhoneExistsAsync(CommandComplex, "0671234567", default));

        var telegram = new CommandTestTelegram();
        var processor = new TelegramCommandProcessor(repository, new CommandHandler(repository, "testbot", CommandComplex),
            telegram, CommandComplex);
        await processor.ProcessAsync(new(30, 42, 42, "/new"), default);
        await processor.ProcessAsync(new(31, 42, 42, "Інше"), default);
        await processor.ProcessAsync(new(32, 42, 42, ComplexLayout.ComplexLabel), default);
        var create = new CommandEnvelope(33, 42, 42, "Тестова заявка зі слухача");
        await processor.ProcessAsync(create, default);
        await processor.ProcessAsync(create, default);

        var request = await repository.GetRequestAsync(42, 33, default);
        Assert.Equal(create.Text, Assert.IsType<MaintenanceRequest>(request).Description);
        Assert.Null(await repository.GetRequestAsync(43, 33, default));
        Assert.Contains((await repository.ListPublicRequestsAsync(CommandComplex, null, default)).Items, row => row.Id == 33);
        Assert.Equal(4, telegram.MessagesSent);
        Assert.True((await repository.GetReplyAsync(33, default))?.Delivered);

        await repository.CommitAsync(new(34, 42, 42, "коментар"),
            new("OK", Comment: new(33, 42, CommandComplex, "Деталі заявки", RegistrationTime)), default);
        await repository.CommitAsync(new(35, 42, 42, "/delete"),
            new("OK", Deletion: new(33, 42, CommandComplex, 1)), default);
        Assert.Null(await repository.GetRequestAsync(42, 33, default));
        var events = await database.Readout.RequestEvents.Where(row => row.RequestId == 33).ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Contains(events, row => row is { Action: "ResidentComment", Resolution: "Деталі заявки", ActorId: 42 });
        Assert.Equal(2, await database.Readout.Notifications.CountAsync(row => row.Kind == "LOCAL_PENDING"));
    }

    [PostgresFact]
    public async Task CommandRepositoryRollsBackConflictingWritesAndRemovesPersonalData()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var repository = await PostgresCommandRepository.ConnectAsync(database, CommandComplex, "testbot", default);
        var profile = await RegisterCommandResidentAsync(repository, 42);
        var invalid = new MaintenanceRequest(50, 43, CommandComplex, "Інше", "", "Проблема", RegistrationTime,
            Scope: RequestScope.Complex);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CommitAsync(new(50, 42, 42, "text"),
            new("invalid", NewRequest: invalid, Lookup: new(new(42, CommandComplex, true, 1), null)), default));
        Assert.Null(await repository.GetReplyAsync(50, default));
        Assert.Null(await repository.GetRequestLookupAsync(42, default));

        var deleted = profile with
        {
            Stage = RegistrationStage.Deleted,
            PersonalDataDeleted = true,
            PersonalDataDeletedAt = RegistrationTime,
            FirstName = null,
            LastName = null,
            Phone = null,
            Email = null,
            BuildingNumber = null,
            ApartmentNumber = null,
            ConsentAt = null,
            ConsentVersion = null,
            Version = profile.Version + 1
        };
        await repository.CommitAsync(new(51, 42, 42, "/delete_profile"), new("OK",
            ProfileDialog: new(new(42, CommandComplex, ProfileAction.Delete, 1, profile.Version, null, Active: false), null),
            Profile: new(deleted, profile.Version)), default);
        Assert.False(await repository.IsResidentAsync(42, CommandComplex, default));
        Assert.False(await repository.ResidentPhoneExistsAsync(CommandComplex, profile.Phone!, default));
        Assert.True((await repository.GetRegistrationAsync(42, default))?.PersonalDataDeleted);
        Assert.Empty(await database.Readout.ResidentConsents.ToListAsync());
        Assert.NotNull(await repository.GetRequestAsync(42, 6, default));
    }

    private static async Task<ResidentRegistration> RegisterCommandResidentAsync(PostgresCommandRepository repository, long userId)
    {
        var initial = new ResidentRegistration("local-profile", userId, CommandComplex, RegistrationStage.AwaitingEmail, 1,
            RegistrationTime, RegistrationTime, Phone: "+380671234567", FirstName: "Іван", LastName: "Мешканець",
            BuildingNumber: "4Б", ApartmentNumber: "12", ConsentAt: RegistrationTime, ConsentVersion: "test");
        await repository.CommitAsync(new(20, userId, userId, "/start"), new("OK", Registration: new(initial, null)), default);
        var approved = initial with { Stage = RegistrationStage.Approved, Version = 2, ApprovedAt = RegistrationTime };
        await repository.CommitAsync(new(21, userId, userId, "Пропустити"), new("OK",
            Registration: new(approved, 1, AutomaticallyApproved: true)), default);
        return approved;
    }

    [PostgresFact]
    public async Task OwnerKeyOrdersAndAdditionalApartmentsUseRelationalRows()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var repository = await PostgresCommandRepository.ConnectAsync(database, CommandComplex, "testbot", default);
        var profile = await RegisterCommandResidentAsync(repository, 42);
        var order = new MaintenanceRequest(60, 42, CommandComplex, RequestCategories.KeyOrder, "", "Ключ", RegistrationTime,
            KeyQuantity: 1, KeyItems: [new("Магніт", profile.Phone!)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CommitAsync(new(60, 42, 42, "Завершити"),
            new("OK", NewRequest: order, OwnerProfileVersion: profile.Version), default));
        Assert.Null(await repository.GetReplyAsync(60, default));

        await database.Readout.Residents.Where(row => row.TelegramUserId == 42).ExecuteUpdateAsync(set =>
            set.SetProperty(row => row.ResidentType, ResidentType.Owner).SetProperty(row => row.Version, 3));
        await repository.CommitAsync(new(60, 42, 42, "Завершити"), new("OK", NewRequest: order, OwnerProfileVersion: 3), default);
        Assert.Equal(profile.Phone, Assert.Single((await repository.GetRequestAsync(42, 60, default))!.KeyItems!).Phone);
        Assert.DoesNotContain((await repository.ListPublicRequestsAsync(CommandComplex, null, default)).Items, row => row.Id == 60);

        var apartment = new OwnerApartment("additional", "4В", 2, RegistrationTime);
        var submission = new OwnerApartments(CommandComplex, 42, profile.Id, 1, [apartment]);
        await repository.CommitAsync(new(61, 42, 42, "2"), new("OK", Apartment: new(submission, null, 3, apartment.Id)), default);
        Assert.Equal(apartment, Assert.Single((await repository.GetOwnerApartmentsAsync(42, default))!.Items));
        Assert.Single(await database.Readout.ResidentApartments.ToListAsync());
    }

    private sealed class CommandTestTelegram : ITelegramClient
    {
        public int MessagesSent { get; private set; }
        public Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken, bool force = false) =>
            Task.CompletedTask;

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
        {
            MessagesSent++;
            return Task.CompletedTask;
        }
    }
}
