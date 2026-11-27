using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OsbbRequests.Postgres;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    [PostgresFact]
    public async Task AdministratorAuditMigrationPreservesPermissionsAndTimestamp()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var context = database.Readout;
        await context.Database.GetService<IMigrator>().MigrateAsync("20261127100808_RemoveObsoleteImportedRecords");
        const string snapshot = """{"ComplexRequests":true,"KeyOrders":false,"RegisteredResidents":true,"PendingResidents":false,"Messages":false}""";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO administrator_events ("SourceId", "Key", "AdministratorId", "Data")
            VALUES (1, 'audit', 'test-user', jsonb_build_object(
                'action', 'AdminPermissionsChanged', 'actor', 'audit@example.invalid', 'createdAt', '2026-11-26T12:00:00+02:00',
                'permissions', {snapshot}::text));
            """);

        await context.Database.MigrateAsync();

        var preserved = await context.Database.SqlQueryRaw<bool>("""
            SELECT "Action" = 'AdminPermissionsChanged' AND "Actor" = 'audit@example.invalid'
                AND "CreatedAt" = '2026-11-26T10:00:00Z'::timestamptz
                AND "ComplexRequests" AND NOT "KeyOrders" AND "RegisteredResidents" AND NOT "PendingResidents" AND NOT "Messages" AS "Value"
            FROM administrator_events WHERE "Key" = 'audit'
            """).SingleAsync();
        Assert.True(preserved);
        Assert.False(await context.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                           WHERE table_schema = 'public' AND table_name = 'administrator_events' AND column_name = 'Data') AS "Value"
            """).SingleAsync());
        await context.Database.GetService<IMigrator>().MigrateAsync("20261127100808_RemoveObsoleteImportedRecords");
        Assert.True(await context.Database.SqlQueryRaw<bool>("""
            SELECT ("Data" ->> 'permissions')::jsonb -> 'KeyOrders' = 'false'::jsonb AS "Value"
            FROM administrator_events WHERE "Key" = 'audit'
            """).SingleAsync());
    }

    [PostgresFact]
    public async Task AdministratorPermissionAuditKeepsTheSnapshotAtEachChange()
    {
        await using var database = await TestDatabase.CreateAsync();
        var context = database.Readout;
        context.Administrators.Add(new() { SourceId = 1, UserId = "target", IsActive = true, Version = 1 });
        await context.SaveChangesAsync();
        var store = new PostgresAdminStore(database, new("osbb-requests-dev"));
        var actor = (await store.GetAdminAsync("test-user", default))!;
        var target = (await store.GetAdminAsync("target", default))!;

        await store.SetPermissionsAsync(actor, target, new(1, new(true, false, true, false, false)), default);
        await store.SetPermissionsAsync(actor, target, new(2, new(false, true, false, true, true)), default);

        var events = await context.AdministratorEvents.AsNoTracking()
            .Where(audit => audit.AdministratorId == "target")
            .OrderBy(audit => audit.CreatedAt).ToListAsync();
        Assert.Collection(events,
            first => Assert.Equal((true, false, true, false, false),
                (first.ComplexRequests, first.KeyOrders, first.RegisteredResidents, first.PendingResidents, first.Messages)),
            second => Assert.Equal((false, true, false, true, true),
                (second.ComplexRequests, second.KeyOrders, second.RegisteredResidents, second.PendingResidents, second.Messages)));
    }

    [PostgresFact]
    public async Task AdministratorRoleMigrationBackfillsBooleanValuesAndApiUsesTheNewColumn()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var context = database.Readout;
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127140733_NormalizeLegacyRegistrationHistory");
        context.Administrators.AddRange(
            new() { SourceId = 1, UserId = "no-snapshot" },
            new() { SourceId = 1, UserId = "false-flag" },
            new() { SourceId = 1, UserId = "string-flag" });
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO source_records ("SourceId","Table","Key","Hash","Data","LastSeenAt","ProjectionVersion")
            VALUES (1,'administrators','test-user','fixture','{{"IsSuperAdmin":true}}',now(),2),
                   (1,'administrators','false-flag','fixture','{{"IsSuperAdmin":false}}',now(),2),
                   (1,'administrators','string-flag','fixture','{{"IsSuperAdmin":"true"}}',now(),2);
            """);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await migrator.MigrateAsync("20261126111056_AddLocalNotificationDelivery");
        await migrator.MigrateAsync("20261127140733_NormalizeLegacyRegistrationHistory");

        var granted = await context.Administrators.Where(admin => admin.IsSuperAdmin)
            .Select(admin => admin.UserId).ToListAsync();
        Assert.Equal("test-user", Assert.Single(granted));

        await context.Database.ExecuteSqlRawAsync("DELETE FROM source_records");
        await context.Database.MigrateAsync();
        var store = new PostgresAdminStore(database, new("osbb-requests-dev"));
        Assert.True((await store.GetAdminAsync("test-user", default))?.IsSuperAdmin);
        Assert.False((await store.GetAdminAsync("string-flag", default))?.IsSuperAdmin);
    }
}
