using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OsbbRequests.DataImport.Storage;

namespace OsbbRequests.Postgres.Storage.Migrations;

[DbContext(typeof(DataContext))]
[Migration("20261127100808_RemoveObsoleteImportedRecords")]
public sealed class RemoveObsoleteImportedRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");
        migrationBuilder.Sql("""
            WITH records AS (
                SELECT "SourceId", "Table", "Key", "Data",
                       split_part("Key", chr(31), 1) AS pk,
                       split_part("Key", chr(31), 2) AS sk
                FROM source_records
                WHERE "Table" IN ('command-data', 'residents')
            ),
            obsolete AS (
                SELECT r."SourceId", r."Table", r."Key"
                FROM records AS r
                WHERE r."Table" = 'residents'
                  AND r.pk LIKE 'COMPLEX#%'
                  AND split_part(r.pk, '#', 3) IN ('DIRECTORY_ROW', 'PHONE_ENTRY', 'DIRECTORY_COUNT')
                UNION ALL
                SELECT r."SourceId", r."Table", r."Key"
                FROM records AS r
                WHERE r."Table" = 'command-data'
                  AND (
                      (r.pk LIKE 'UPDATE#%' AND r.sk = 'REPLY' AND r."Data" -> 'delivered' = 'true'::jsonb)
                      OR (r.pk LIKE 'USER#%' AND (r.sk LIKE 'COMPLEX_CURSOR#%' OR r.sk = 'PAGE_NAVIGATION'))
                  )
                UNION ALL
                SELECT r."SourceId", r."Table", r."Key"
                FROM records AS r
                JOIN notifications AS n ON n."SourceId" = r."SourceId"
                     AND n."Key" = r."Key"
                     AND n."Kind" = split_part(r.sk, '#', 1)
                     AND n."Data" = r."Data"
                WHERE r."Table" = 'command-data' AND r.pk LIKE 'NOTIFICATIONS#%'
                  AND (r.sk LIKE 'RESULT#%' OR r.sk LIKE 'MESSAGE#%')
            )
            DELETE FROM source_records AS source
            USING obsolete AS r
            WHERE source."SourceId" = r."SourceId" AND source."Table" = r."Table" AND source."Key" = r."Key";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restoring the removed source rows requires a database backup.
    }
}
