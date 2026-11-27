using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeLegacyRegistrationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");

            migrationBuilder.CreateTable(
                name: "resident_membership_history",
                columns: table => new
                {
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<long>(type: "bigint", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    RegistrationId = table.Column<string>(type: "text", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewedBy = table.Column<string>(type: "text", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_membership_history", x => new { x.SourceId, x.Key });
                    table.ForeignKey(
                        name: "FK_resident_membership_history_complexes_SourceId_ComplexId",
                        columns: x => new { x.SourceId, x.ComplexId },
                        principalTable: "complexes",
                        principalColumns: new[] { "SourceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resident_membership_history_import_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "import_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "resident_registration_snapshots",
                columns: table => new
                {
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    ComplexId = table.Column<long>(type: "bigint", nullable: false),
                    RegistrationId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false),
                    ResidentType = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "text", nullable: true),
                    ConsentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsentVersion = table.Column<string>(type: "text", nullable: true),
                    ResumeAfterConsent = table.Column<string>(type: "text", nullable: true),
                    BuildingNumber = table.Column<string>(type: "text", nullable: true),
                    ApartmentNumber = table.Column<string>(type: "text", nullable: true),
                    FirstName = table.Column<string>(type: "text", nullable: true),
                    LastName = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    PersonalDataDeleted = table.Column<bool>(type: "boolean", nullable: true),
                    PersonalDataDeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TypeBeforeAddress = table.Column<bool>(type: "boolean", nullable: true),
                    TypeChangeCount = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_registration_snapshots", x => new { x.SourceId, x.ComplexId, x.RegistrationId, x.Version });
                    table.ForeignKey(
                        name: "FK_resident_registration_snapshots_complexes_SourceId_ComplexId",
                        columns: x => new { x.SourceId, x.ComplexId },
                        principalTable: "complexes",
                        principalColumns: new[] { "SourceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_resident_registration_snapshots_import_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "import_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_resident_membership_history_SourceId_ComplexId",
                table: "resident_membership_history",
                columns: new[] { "SourceId", "ComplexId" });

            migrationBuilder.CreateIndex(
                name: "IX_resident_registration_snapshots_ComplexId_TelegramUserId",
                table: "resident_registration_snapshots",
                columns: new[] { "ComplexId", "TelegramUserId" });

            migrationBuilder.Sql("""
                CREATE TEMP TABLE legacy_registration_candidates ON COMMIT DROP AS
                WITH source AS (
                    SELECT "SourceId", "Table", "Key", "Data",
                           split_part("Key", chr(31), 1) AS pk, split_part("Key", chr(31), 2) AS sk
                    FROM source_records
                )
                SELECT s."SourceId", s."Table", s."Key",
                       CASE WHEN s."Table" = 'residents' THEN
                           s."Data" - ARRAY['complexId','pk','recordData','recordGroup','recordOrder','sourcePk','sourceSk','version'] = '{}'::jsonb
                           AND (s."Data" -> 'recordData') - ARRAY['pk','sk','payload','version'] = '{}'::jsonb
                           ELSE s."Data" - ARRAY['pk','sk','payload','version'] = '{}'::jsonb END AS known_shape,
                       CASE WHEN s."Table" = 'residents' THEN s."Data" #>> '{recordData,version}' ELSE s."Data" ->> 'version' END AS source_version,
                       (CASE WHEN s."Table" = 'residents' THEN s."Data" #>> '{recordData,payload}' ELSE s."Data" ->> 'payload' END)::jsonb AS payload
                FROM source s
                WHERE (s."Table" = 'residents' AND s."Data" ->> 'sourcePk' LIKE 'REGISTRATION#%' AND s."Data" ->> 'sourceSk' = 'META')
                   OR (s."Table" = 'command-data' AND (
                       (s.pk LIKE 'REGISTRATION#%' AND (s.sk = 'META' OR s.sk LIKE 'META#%'))
                       OR (s.pk LIKE 'USER#%' AND s.sk = 'REGISTRATION')
                       OR (s.pk LIKE 'BUILDING#%' AND (s.sk LIKE 'RESIDENT#%' OR s.sk LIKE 'REGISTRATION#%'))));

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM legacy_registration_candidates AS candidate
                        LEFT JOIN complexes c ON c."SourceId" = candidate."SourceId" AND c."ExternalId" = payload ->> 'BuildingId'
                        WHERE known_shape IS DISTINCT FROM true OR c."Id" IS NULL
                           OR nullif(payload ->> 'Id','') IS NULL OR payload ->> 'UserId' IS NULL
                           OR payload ->> 'Version' IS NULL OR payload ->> 'Stage' IS NULL
                           OR source_version::integer IS DISTINCT FROM (payload ->> 'Version')::integer
                           OR payload - ARRAY['Id','UserId','BuildingId','Stage','Version','CreatedAt','UpdatedAt','FirstName','LastName',
                              'DecisionReason','BuildingNumber','ApartmentNumber','PersonalDataDeleted','PersonalDataDeletedAt','ApprovedAt',
                              'Email','Phone','ConsentAt','ConsentVersion','ResumeAfterConsent','ResidentType','TypeBeforeAddress','TypeChangeCount'] <> '{}'::jsonb
                    ) OR EXISTS (
                        SELECT 1 FROM legacy_registration_candidates
                        GROUP BY "SourceId", payload ->> 'BuildingId', payload ->> 'Id', (payload ->> 'Version')::integer
                        HAVING count(DISTINCT payload) > 1
                    ) THEN
                        RAISE EXCEPTION 'Unmapped or conflicting legacy registration snapshot';
                    END IF;
                END $$;

                INSERT INTO resident_registration_snapshots (
                    "SourceId","ComplexId","RegistrationId","Version","TelegramUserId","Stage","ResidentType","CreatedAt","UpdatedAt",
                    "ApprovedAt","DecisionReason","ConsentAt","ConsentVersion","ResumeAfterConsent","BuildingNumber","ApartmentNumber",
                    "FirstName","LastName","Phone","Email","PersonalDataDeleted","PersonalDataDeletedAt","TypeBeforeAddress","TypeChangeCount")
                SELECT DISTINCT candidate."SourceId",c."Id",payload ->> 'Id',(payload ->> 'Version')::integer,(payload ->> 'UserId')::bigint,
                    payload ->> 'Stage',payload ->> 'ResidentType',(payload ->> 'CreatedAt')::timestamptz,(payload ->> 'UpdatedAt')::timestamptz,
                    (payload ->> 'ApprovedAt')::timestamptz,CASE WHEN NOT redact THEN payload ->> 'DecisionReason' END,
                    (payload ->> 'ConsentAt')::timestamptz,payload ->> 'ConsentVersion',payload ->> 'ResumeAfterConsent',
                    CASE WHEN NOT redact THEN payload ->> 'BuildingNumber' END,CASE WHEN NOT redact THEN payload ->> 'ApartmentNumber' END,
                    CASE WHEN NOT redact THEN payload ->> 'FirstName' END,CASE WHEN NOT redact THEN payload ->> 'LastName' END,
                    CASE WHEN NOT redact THEN payload ->> 'Phone' END,CASE WHEN NOT redact THEN payload ->> 'Email' END,
                    (payload ->> 'PersonalDataDeleted')::boolean,(payload ->> 'PersonalDataDeletedAt')::timestamptz,
                    (payload ->> 'TypeBeforeAddress')::boolean,(payload ->> 'TypeChangeCount')::integer
                FROM legacy_registration_candidates AS candidate
                JOIN complexes c ON c."SourceId" = candidate."SourceId" AND c."ExternalId" = payload ->> 'BuildingId'
                LEFT JOIN residents r ON r."ComplexId" = c."Id" AND r."TelegramUserId" = (payload ->> 'UserId')::bigint
                CROSS JOIN LATERAL (
                    SELECT coalesce(r."PersonalDataDeleted" OR r."Stage" = 'Deleted',false)
                           OR coalesce((payload ->> 'PersonalDataDeleted')::boolean OR payload ->> 'Stage' = 'Deleted',false) AS redact
                ) AS privacy;

                CREATE TEMP TABLE legacy_membership_candidates ON COMMIT DROP AS
                SELECT s."SourceId",s."Key",s."Data",c."Id" AS complex_id,
                       split_part(split_part(s."Key", chr(31), 1), '#', 2)::bigint AS user_id
                FROM source_records s
                LEFT JOIN complexes c ON c."SourceId" = s."SourceId" AND c."ExternalId" = s."Data" ->> 'buildingId'
                WHERE s."Table" = 'command-data' AND split_part(s."Key", chr(31), 1) LIKE 'USER#%'
                  AND split_part(s."Key", chr(31), 2) = 'MEMBERSHIP';

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM legacy_membership_candidates
                        WHERE complex_id IS NULL OR jsonb_typeof("Data" -> 'active') IS DISTINCT FROM 'boolean'
                           OR "Data" - ARRAY['pk','sk','buildingId','registrationId','active','reviewedBy','reviewedAt'] <> '{}'::jsonb
                    ) THEN
                        RAISE EXCEPTION 'Unmapped legacy membership history';
                    END IF;
                END $$;

                INSERT INTO resident_membership_history ("SourceId","Key","ComplexId","TelegramUserId","RegistrationId","Active","ReviewedBy","ReviewedAt")
                SELECT "SourceId","Key",complex_id,user_id,"Data" ->> 'registrationId',("Data" ->> 'active')::boolean,
                       "Data" ->> 'reviewedBy',("Data" ->> 'reviewedAt')::timestamptz
                FROM legacy_membership_candidates;

                DELETE FROM source_records AS source
                USING legacy_registration_candidates AS candidate
                JOIN complexes c ON c."SourceId" = candidate."SourceId" AND c."ExternalId" = candidate.payload ->> 'BuildingId'
                JOIN resident_registration_snapshots snapshot ON snapshot."SourceId" = candidate."SourceId" AND snapshot."ComplexId" = c."Id"
                   AND snapshot."RegistrationId" = candidate.payload ->> 'Id' AND snapshot."Version" = (candidate.payload ->> 'Version')::integer
                WHERE source."SourceId" = candidate."SourceId" AND source."Table" = candidate."Table" AND source."Key" = candidate."Key";

                DELETE FROM source_records AS source
                USING resident_membership_history AS history
                WHERE source."SourceId" = history."SourceId" AND source."Table" = 'command-data' AND source."Key" = history."Key";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");
            migrationBuilder.Sql("""
                WITH archives AS (
                    SELECT s."SourceId", 'REGISTRATION#' || s."RegistrationId" AS pk,
                           'META#' || s."ComplexId" || '#' || s."Version" AS sk, s."Version" AS version,
                           jsonb_build_object('Id',s."RegistrationId",'Version',s."Version",'BuildingId',c."ExternalId",'UserId',s."TelegramUserId",
                               'Stage',s."Stage",'ResidentType',s."ResidentType",'CreatedAt',s."CreatedAt",'UpdatedAt',s."UpdatedAt",
                               'ApprovedAt',s."ApprovedAt",'DecisionReason',s."DecisionReason",'ConsentAt',s."ConsentAt",'ConsentVersion',s."ConsentVersion",
                               'ResumeAfterConsent',s."ResumeAfterConsent",'BuildingNumber',s."BuildingNumber",'ApartmentNumber',s."ApartmentNumber",
                               'FirstName',s."FirstName",'LastName',s."LastName",'Phone',s."Phone",'Email',s."Email",
                               'PersonalDataDeleted',s."PersonalDataDeleted",'PersonalDataDeletedAt',s."PersonalDataDeletedAt",
                               'TypeBeforeAddress',s."TypeBeforeAddress",'TypeChangeCount',s."TypeChangeCount") AS payload
                    FROM resident_registration_snapshots s
                    JOIN complexes c ON c."SourceId" = s."SourceId" AND c."Id" = s."ComplexId"
                ), records AS (
                    SELECT a."SourceId",a.pk || chr(31) || a.sk AS key,
                           jsonb_build_object('pk',a.pk,'sk',a.sk,'version',a.version,'payload',a.payload::text) AS data
                    FROM archives a
                    UNION ALL
                    SELECT h."SourceId",h."Key",
                           jsonb_strip_nulls(jsonb_build_object('pk',split_part(h."Key",chr(31),1),'sk','MEMBERSHIP',
                               'buildingId',c."ExternalId",'registrationId',h."RegistrationId",'active',h."Active",
                               'reviewedBy',h."ReviewedBy",'reviewedAt',h."ReviewedAt"))
                    FROM resident_membership_history h
                    JOIN complexes c ON c."SourceId" = h."SourceId" AND c."Id" = h."ComplexId"
                )
                INSERT INTO source_records ("SourceId","Table","Key","Data","Hash","LastSeenAt","ProjectionVersion")
                SELECT "SourceId",'command-data',key,data,encode(sha256(convert_to(data::text,'UTF8')),'hex'),now(),0
                FROM records;
                """);

            migrationBuilder.DropTable(
                name: "resident_membership_history");

            migrationBuilder.DropTable(
                name: "resident_registration_snapshots");
        }
    }
}
