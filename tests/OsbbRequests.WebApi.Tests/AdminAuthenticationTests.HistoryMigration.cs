using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace OsbbRequests.WebApi.Tests;

public sealed partial class AdminAuthenticationTests
{
    [PostgresFact]
    public async Task HistoryMigrationPreservesResidentKeyRemovalAndRequestSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync(useMigrations: true);
        var context = database.Readout;
        await context.Database.GetService<IMigrator>().MigrateAsync("20261127112349_NormalizeAdministratorEvents");
        const string resident = """{"Actor":"admin@example.invalid","CreatedAt":"2026-11-26T12:00:00Z","KeyChange":{"RequestId":6,"Previous":{"Type":"Магніт","SerialNumber":"test-key"},"Current":null}}""";
        const string changes = """{"AssignedTo":null,"BuildingNumber":"4Б","Floor":8,"PlannedDate":null,"Priority":"High","Scope":"Building","Visibility":1}""";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO resident_events ("SourceId","Key","RegistrationId","EventKey","Data")
            VALUES (1,'resident-audit','registration','EVENT#TYPE#1',jsonb_build_object('payload',{resident}::text));
            INSERT INTO request_events ("SourceId","Key","RequestId","EventKey","CreatedAt","Data")
            VALUES (1,'request-audit',6,'EVENT#1','2026-11-26T12:00:00Z',
                    jsonb_build_object('actor','admin@example.invalid','status','Completed','resolution',NULL::text,'changes',{changes}::text));
            """);

        await context.Database.MigrateAsync();

        Assert.True(await context.Database.SqlQueryRaw<bool>("""
            SELECT "Actor" = 'admin@example.invalid' AND "KeyRequestId" = 6
                   AND "PreviousKeySerialNumber" = 'test-key' AND "CurrentKeyType" IS NULL AS "Value"
            FROM resident_events WHERE "Key" = 'resident-audit'
            """).SingleAsync());
        Assert.True(await context.Database.SqlQueryRaw<bool>("""
            SELECT e."Status" = 'Completed' AND e."HasResolution" AND e."Resolution" IS NULL
                   AND s."Floor" = 8 AND NOT s."HasEntrance" AND s."Visibility" = 1 AS "Value"
            FROM request_events e JOIN request_event_snapshots s USING ("SourceId", "Key") WHERE e."Key" = 'request-audit'
            """).SingleAsync());
    }
}
