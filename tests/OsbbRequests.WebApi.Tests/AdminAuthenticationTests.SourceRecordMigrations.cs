using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    [PostgresFact]
    public async Task SourceCleanupPreservesDifferencesAndArchivesRegistrationHistory()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var context = database.Readout;
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261127134326_NormalizeNotifications");
        const string profile = """
            {"Id":"old-ref","UserId":42,"BuildingId":"osbb-requests-dev","Stage":"Approved","Version":2,
             "CreatedAt":"2026-10-01T00:00:00Z","UpdatedAt":"2026-10-02T00:00:00Z","ConsentVersion":"v1"}
            """;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO api_daily_statistics ("SourceId","Date","Endpoint","Requests","ReadUnits","WriteUnits","DurationMs")
            VALUES (1,'2026-10-01','GET /same',3,0,0,0),(1,'2026-10-01','GET /different',4,0,0,0);
            INSERT INTO source_records ("SourceId","Table","Key","Data","Hash","LastSeenAt","ProjectionVersion")
            VALUES (1,'statistics','API'||chr(31)||'2026-10-01#GET /same',jsonb_build_object('requests',3),'test',now(),2),
                   (1,'statistics','API'||chr(31)||'2026-10-01#GET /different',jsonb_build_object('requests',3),'test',now(),2),
                   (1,'command-data','REGISTRATION#old-ref'||chr(31)||'META',jsonb_build_object('payload',{profile}::text,'version',2),'test',now(),2),
                   (1,'command-data','USER#42'||chr(31)||'REGISTRATION',jsonb_build_object('payload',{profile}::text,'version',2),'test',now(),2),
                   (1,'command-data','USER#42'||chr(31)||'MEMBERSHIP',
                    jsonb_build_object('buildingId','osbb-requests-dev','active',true,'reviewedBy','reviewer@example.invalid',
                                       'reviewedAt','2026-10-02T00:00:00Z'),'test',now(),2);
            """);

        await migrator.MigrateAsync("20261127140733_NormalizeLegacyRegistrationHistory");

        var statistic = await context.SourceRecords.SingleAsync(row => row.Table == "statistics");
        Assert.EndsWith("GET /different", statistic.Key, StringComparison.Ordinal);
        Assert.False(await context.SourceRecords.AnyAsync(row => row.Table == "command-data"));
        var snapshot = await context.ResidentRegistrationSnapshots.SingleAsync();
        Assert.Equal("old-ref", snapshot.RegistrationId);
        Assert.Equal("v1", snapshot.ConsentVersion);
        var membership = await context.ResidentMembershipHistory.SingleAsync();
        Assert.Equal("reviewer@example.invalid", membership.ReviewedBy);
        Assert.Null(membership.RegistrationId);

        context.ChangeTracker.Clear();
        await migrator.MigrateAsync("20261127140605_RemoveProjectedSourceRecords");
        Assert.Equal(2, await context.SourceRecords.CountAsync(row => row.Table == "command-data"));
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261127150535_DropEmptySourceRecords"));
        await context.Database.ExecuteSqlRawAsync("DELETE FROM source_records");
        await context.Database.MigrateAsync();
        Assert.Equal(1, await context.ResidentRegistrationSnapshots.CountAsync());
        Assert.Equal(1, await context.ResidentMembershipHistory.CountAsync());
        Assert.Null(await context.Database.SqlQueryRaw<string?>("SELECT to_regclass('public.source_records')::text AS \"Value\"").SingleAsync());
        await migrator.MigrateAsync("20261127140733_NormalizeLegacyRegistrationHistory");
        Assert.Empty(await context.SourceRecords.ToListAsync());
        await context.Database.MigrateAsync();
        Assert.Null(await context.Database.SqlQueryRaw<string?>("SELECT to_regclass('public.source_records')::text AS \"Value\"").SingleAsync());
    }
}
