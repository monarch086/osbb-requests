using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OsbbRequests.Postgres;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    [PostgresFact]
    public async Task AdministratorRoleMigrationBackfillsBooleanValuesAndApiUsesTheNewColumn()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var context = database.Readout;
        context.Administrators.AddRange(
            new() { SourceId = 1, UserId = "no-snapshot" },
            new() { SourceId = 1, UserId = "false-flag" },
            new() { SourceId = 1, UserId = "string-flag" });
        context.SourceRecords.AddRange(
            new() { SourceId = 1, Table = "administrators", Key = "false-flag", Hash = "fixture", Data = """{"IsSuperAdmin":false}""" },
            new() { SourceId = 1, Table = "administrators", Key = "string-flag", Hash = "fixture", Data = """{"IsSuperAdmin":"true"}""" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.GetService<IMigrator>().MigrateAsync("20261126111056_AddLocalNotificationDelivery");
        await context.Database.MigrateAsync();

        var granted = await context.Administrators.Where(admin => admin.IsSuperAdmin)
            .Select(admin => admin.UserId).ToListAsync();
        Assert.Equal("test-user", Assert.Single(granted));

        await context.SourceRecords.Where(row => row.Table == "administrators").ExecuteDeleteAsync();
        var store = new PostgresAdminStore(database, new("eu-central-1", "dev", "osbb-requests-dev"));
        Assert.True((await store.GetAdminAsync("test-user", default))?.IsSuperAdmin);
        Assert.False((await store.GetAdminAsync("string-flag", default))?.IsSuperAdmin);
    }
}
