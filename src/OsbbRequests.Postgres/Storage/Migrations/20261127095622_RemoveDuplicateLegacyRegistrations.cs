using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OsbbRequests.DataImport.Storage;

namespace OsbbRequests.Postgres.Storage.Migrations;

[DbContext(typeof(DataContext))]
[Migration("20261127095622_RemoveDuplicateLegacyRegistrations")]
public sealed class RemoveDuplicateLegacyRegistrations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");
        migrationBuilder.Sql("""
            WITH records AS (
                SELECT s."SourceId", s."Table", s."Key",
                       CASE WHEN s."Table" = 'residents' THEN s."Data" ->> 'sourcePk'
                            ELSE split_part(s."Key", chr(31), 1) END AS pk,
                       CASE WHEN s."Table" = 'residents' THEN s."Data" ->> 'sourceSk'
                            ELSE split_part(s."Key", chr(31), 2) END AS sk,
                       CASE WHEN s."Table" = 'residents' THEN s."Data" -> 'recordData'
                            ELSE s."Data" END AS data
                FROM source_records AS s
                WHERE s."Table" = 'command-data'
                   OR (s."Table" = 'residents' AND s."Key" LIKE 'COMPLEX#%#RECORD#%')
            ),
            profile_copies AS (
                SELECT r.*, CASE WHEN pg_input_is_valid(r.data ->> 'payload', 'jsonb')
                                THEN (r.data ->> 'payload')::jsonb END AS profile
                FROM records AS r
                WHERE (r.pk LIKE 'USER#%' AND r.sk = 'REGISTRATION')
                   OR (r.pk LIKE 'BUILDING#%' AND (r.sk LIKE 'RESIDENT#%' OR r.sk LIKE 'REGISTRATION#%'))
            ),
            duplicates AS (
                SELECT old."SourceId", old."Table", old."Key"
                FROM profile_copies AS old
                JOIN complexes AS c ON c."SourceId" = old."SourceId" AND c."ExternalId" = old.profile ->> 'BuildingId'
                JOIN residents AS resident ON resident."ComplexId" = c."Id"
                     AND resident."TelegramUserId"::text = old.profile ->> 'UserId'
                JOIN source_records AS current ON current."SourceId" = old."SourceId" AND current."Table" = 'residents'
                     AND current."Key" = 'COMPLEX#' || c."ExternalId" || '#USER#' || resident."TelegramUserId" || chr(31)
                WHERE old.data - ARRAY['pk', 'sk', 'payload', 'version'] = '{}'::jsonb
                  AND old.profile ->> 'Id' = resident."RegistrationId"
                  AND old.data -> 'version' = to_jsonb(resident."Version")
                  AND old.data -> 'version' = current."Data" -> 'version'
                  AND old.profile = CASE WHEN pg_input_is_valid(current."Data" ->> 'payload', 'jsonb')
                                        THEN (current."Data" ->> 'payload')::jsonb END
                  AND (old.pk = 'USER#' || resident."TelegramUserId"
                       OR (old.pk = 'BUILDING#' || c."ExternalId"
                           AND old.sk IN ('RESIDENT#' || lpad(resident."TelegramUserId"::text, 20, '0'),
                                          'REGISTRATION#' || resident."RegistrationId")))
                UNION ALL
                SELECT old."SourceId", old."Table", old."Key"
                FROM records AS old
                JOIN resident_events AS event ON event."SourceId" = old."SourceId"
                     AND old.pk = 'REGISTRATION#' || event."RegistrationId"
                     AND old.sk = event."EventKey"
                     AND old.data - ARRAY['pk', 'sk'] = event."Data" - ARRAY['pk', 'sk']
                WHERE old."Table" = 'command-data' AND old.sk LIKE 'EVENT#%'
                UNION ALL
                SELECT old."SourceId", old."Table", old."Key"
                FROM records AS old
                JOIN complexes AS c ON c."SourceId" = old."SourceId" AND c."ExternalId" = old.data ->> 'buildingId'
                JOIN resident_memberships AS member ON member."SourceId" = old."SourceId" AND member."ComplexId" = c."Id"
                     AND old.pk = 'USER#' || member."TelegramUserId"
                WHERE old."Table" = 'command-data' AND old.sk = 'MEMBERSHIP'
                  AND old.data - ARRAY['pk', 'sk', 'buildingId', 'active', 'registrationId', 'reviewedBy', 'reviewedAt'] = '{}'::jsonb
                  AND old.data -> 'active' = to_jsonb(member."Active")
                  AND old.data ->> 'registrationId' IS NOT DISTINCT FROM member."RegistrationId"
                  AND old.data ->> 'reviewedBy' IS NOT DISTINCT FROM member."ReviewedBy"
                  AND (old.data ->> 'reviewedAt' IS NULL AND member."ReviewedAt" IS NULL
                       OR CASE WHEN pg_input_is_valid(old.data ->> 'reviewedAt', 'timestamp with time zone')
                               THEN (old.data ->> 'reviewedAt')::timestamptz = member."ReviewedAt" END)
            )
            DELETE FROM source_records AS source
            USING duplicates AS duplicate
            WHERE source."SourceId" = duplicate."SourceId" AND source."Table" = duplicate."Table" AND source."Key" = duplicate."Key";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // This data-only migration removes copies whose contents are already retained.
    }
}
