using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OsbbRequests.CommandProcessor.Models;
using OsbbRequests.CommandProcessor.Notifications;
using OsbbRequests.CommandProcessor.Telegram;
using OsbbRequests.DataImport.Storage;
using OsbbRequests.Domain.Models;
using OsbbRequests.Postgres.Bot;
using OsbbRequests.Services.Notifications;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    [PostgresFact]
    public async Task NotificationMigrationPreservesPendingDeliveryAndLegacyExpiry()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var context = database.Readout;
        await context.Database.GetService<IMigrator>().MigrateAsync("20261127121810_NormalizeResidentAndRequestEvents");
        var now = DateTimeOffset.UtcNow;
        var pending = new ResidentNotification("migration-test", CommandComplex, 42, 6, 2, "Тест черги", now);
        var legacyMessage = $"NOTIFICATIONS#{CommandComplex}\u001fMESSAGE#42#15";
        var legacyResult = $"NOTIFICATIONS#{CommandComplex}\u001fRESULT#migration-test";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications ("SourceId","ComplexId","Key","Kind","Data","DueAt","Attempts")
            VALUES (1,1,'LOCAL#migration-test','LOCAL_PENDING',{JsonSerializer.Serialize(pending)}::jsonb,{now},2),
                   (1,1,{legacyMessage},'MESSAGE',jsonb_build_object('requestId',6),NULL,0),
                   (1,1,{legacyResult},'RESULT',jsonb_build_object('completedAt','2025-01-01T00:00:00Z',
                         'disposition','Delivered','expiresAt',1735689600),NULL,0);
            """);
        await context.Database.MigrateAsync();

        Assert.True(await context.Database.SqlQueryRaw<bool>("""
            SELECT "Disposition" = 'Delivered' AND "LegacyExpiresAt" = '2025-01-01T00:00:00Z'::timestamptz
                   AND "ExpiresAt" IS NULL AS "Value" FROM notifications WHERE "Kind" = 'RESULT'
            """).SingleAsync());
        var scope = await PostgresBotScope.LoadAsync(database, CommandComplex, "testbot", default);
        var repository = new PostgresNotificationRepository(database, scope);
        var item = Assert.Single(await repository.ListDueAsync(now.AddSeconds(1), default));
        Assert.Equal(2, item.Attempts);
        Assert.Equal(pending.Text, item.Notification.Text);
        Assert.Equal(pending.RequestVersion, item.Notification.RequestVersion);
        Assert.True(await context.Notifications.AnyAsync(row => row.Kind == "RESULT"));
        var commands = await PostgresCommandRepository.ConnectAsync(database, CommandComplex, "testbot", default);
        Assert.Equal(6, await commands.GetNotificationRequestAsync(42, 15, default));
    }

    [PostgresFact]
    public async Task LocalNotificationDeliveryRecordsTheTelegramReplyAndKeepsOnlyResults()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var commands = await PostgresCommandRepository.ConnectAsync(database, CommandComplex, "testbot", default);
        await RegisterCommandResidentAsync(commands, 42);
        var notification = new ResidentNotification("update-request", CommandComplex, 42, 6, 3, "Новий коментар", RegistrationTime);
        await InsertNotificationAsync(database.Readout, notification);
        var scope = await PostgresBotScope.LoadAsync(database, CommandComplex, "testbot", default);
        var repository = new PostgresNotificationRepository(database, scope);
        var telegram = new NotificationTestTelegram();
        var processor = new NotificationProcessor(repository, telegram);

        await processor.RunAsync(default);
        await processor.RunAsync(default);

        var message = Assert.Single(telegram.Messages);
        Assert.Equal(42, message.ChatId);
        Assert.Contains("Новий коментар", message.Text);
        Assert.Contains("Відповісти", message.Text);
        Assert.Equal(6, await commands.GetNotificationRequestAsync(42, 123, default));
        Assert.Null(await commands.GetNotificationRequestAsync(43, 123, default));
        var result = await database.Readout.Notifications.AsNoTracking().SingleAsync(row => row.Kind == "LOCAL_DELIVERED");
        Assert.Equal("Delivered", result.Disposition);
        Assert.NotNull(result.CompletedAt);
        Assert.Null(result.Text);
        Assert.Null(result.OwnerId);
        Assert.Null(result.RequestId);
    }

    [PostgresFact]
    public async Task LocalNotificationRepositoryHonorsLeaseAndDurablePause()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var scope = await PostgresBotScope.LoadAsync(database, CommandComplex, "testbot", default);
        var repository = new PostgresNotificationRepository(database, scope);
        var now = DateTimeOffset.UtcNow;
        await InsertNotificationAsync(database.Readout, new("lease-test", CommandComplex, 42, null, null, "Тест", now));

        var item = Assert.Single(await repository.ListDueAsync(now, default));
        Assert.True(await repository.ClaimAsync(item, "old", now, default));
        Assert.False(await repository.ClaimAsync(item, "other", now, default));
        var retryAt = now.AddMinutes(5);
        await repository.PauseAsync(retryAt, default);
        await repository.RetryAsync(item, "old", retryAt, default);
        Assert.Empty(await repository.ListDueAsync(retryAt.AddSeconds(-1), default));
        var retry = Assert.Single(await repository.ListDueAsync(retryAt, default));
        Assert.Equal(1, retry.Attempts);
        Assert.True(await repository.ClaimAsync(retry, "other", retryAt, default));
        await repository.CompleteAsync(retry, "old", "Delivered", retryAt, default);
        Assert.True(await repository.ClaimAsync(retry, "reclaimed", retryAt.AddSeconds(91), default));
    }

    [PostgresFact]
    public async Task LocalBroadcastUsesSubscriptionsAndExistingApprovedResidents()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var commands = await PostgresCommandRepository.ConnectAsync(database, CommandComplex, "testbot", default);
        await RegisterCommandResidentAsync(commands, 42);
        database.Readout.AdministratorNotificationSettings.Add(new()
        {
            SourceId = 1,
            UserId = "test-user",
            TelegramChatId = "101",
            NewResidents = true
        });
        database.Readout.Residents.Add(new()
        {
            ComplexId = 1,
            TelegramUserId = 84,
            Stage = RegistrationStage.Approved,
            ApprovedAt = RegistrationTime.AddDays(1)
        });
        await InsertNotificationAsync(database.Readout, new("broadcast-test", CommandComplex, 0, null, null, "Оголошення",
            RegistrationTime, ResidentBroadcastId: "broadcast"));
        var scope = await PostgresBotScope.LoadAsync(database, CommandComplex, "testbot", default);
        var telegram = new NotificationTestTelegram();
        var processor = new NotificationProcessor(new PostgresNotificationRepository(database, scope), telegram);
        await processor.RunAsync(default);
        await processor.RunAsync(default);

        Assert.Equal(new[] { 42L, 101L }, telegram.Messages.Select(message => message.ChatId).Order().ToArray());
        Assert.Equal("Оголошення", telegram.Messages.Single(message => message.ChatId == 42).Text);
    }

    private static async Task InsertNotificationAsync(DataContext db, ResidentNotification notification)
    {
        db.Notifications.Add(Notification.CreatePending(1, 1, CommandComplex, notification));
        await db.SaveChangesAsync();
    }

    private sealed class NotificationTestTelegram : ITelegramClient
    {
        public List<(long ChatId, string Text)> Messages { get; } = [];
        public Task SetCommandMenuAsync(long chatId, bool registered, CancellationToken cancellationToken, bool force = false) =>
            Task.CompletedTask;

        public async Task<long?> SendNotificationAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            await SendMessageAsync(chatId, text, cancellationToken);
            return 123;
        }

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken, ReplyKeyboard? keyboard = null)
        {
            Messages.Add((chatId, text));
            return Task.CompletedTask;
        }
    }
}
