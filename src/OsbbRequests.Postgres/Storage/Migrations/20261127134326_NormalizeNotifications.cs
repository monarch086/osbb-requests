using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdministratorEvent",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdministratorId",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LegacyExpiresAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OwnerId",
                table: "notifications",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationId",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RequestId",
                table: "notifications",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestVersion",
                table: "notifications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidentBroadcastId",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM notifications AS n
                        JOIN complexes AS c ON c."SourceId" = n."SourceId" AND c."Id" = n."ComplexId"
                        WHERE jsonb_typeof(n."Data") <> 'object'
                           OR CASE n."Kind"
                                WHEN 'LOCAL_PENDING' THEN
                                    n."Data" - ARRAY['Id','ComplexId','OwnerId','RequestId','RequestVersion','Text','CreatedAt',
                                                     'AdministratorEvent','AdministratorId','ResidentBroadcastId','RegistrationId'] <> '{}'::jsonb
                                    OR NULLIF(n."Data" ->> 'Id', '') IS NULL
                                    OR n."Key" <> 'LOCAL#' || (n."Data" ->> 'Id')
                                    OR (n."Data" ->> 'ComplexId') IS DISTINCT FROM c."ExternalId"
                                    OR jsonb_typeof(n."Data" -> 'OwnerId') IS DISTINCT FROM 'number'
                                    OR jsonb_typeof(n."Data" -> 'Text') IS DISTINCT FROM 'string'
                                    OR jsonb_typeof(n."Data" -> 'CreatedAt') IS DISTINCT FROM 'string'
                                    OR (n."Data" ->> 'AdministratorEvent') NOT IN
                                        ('NewResident','NewRequest','NewAdministrator','NewComplexRequest','NewKeyOrder','NewApartmentRegistration')
                                WHEN 'RESULT' THEN n."Data" - ARRAY['pk','sk','completedAt','disposition','expiresAt'] <> '{}'::jsonb
                                WHEN 'MESSAGE' THEN n."Data" - ARRAY['pk','sk','requestId'] <> '{}'::jsonb
                                WHEN 'LOCAL_MESSAGE' THEN n."Data" - 'requestId' <> '{}'::jsonb
                                WHEN 'LOCAL_PAUSE' THEN n."Data" <> '{}'::jsonb
                                ELSE n."Kind" NOT IN ('LOCAL_DELIVERED','LOCAL_UNDELIVERABLE','LOCAL_EXPANDED','LOCAL_SKIPPED')
                                     OR n."Data" - ARRAY['completedAt','disposition'] <> '{}'::jsonb
                           END
                           OR (n."Data" ->> 'pk') <> split_part(n."Key", chr(31), 1)
                           OR (n."Data" ->> 'sk') <> split_part(n."Key", chr(31), 2)
                    ) THEN
                        RAISE EXCEPTION 'Unmapped data in notifications';
                    END IF;
                END $$;

                UPDATE notifications
                SET "OwnerId" = ("Data" ->> 'OwnerId')::bigint,
                    "RequestId" = COALESCE("Data" ->> 'RequestId', "Data" ->> 'requestId')::bigint,
                    "RequestVersion" = ("Data" ->> 'RequestVersion')::integer,
                    "Text" = "Data" ->> 'Text',
                    "CreatedAt" = ("Data" ->> 'CreatedAt')::timestamptz,
                    "AdministratorEvent" = "Data" ->> 'AdministratorEvent',
                    "AdministratorId" = "Data" ->> 'AdministratorId',
                    "ResidentBroadcastId" = "Data" ->> 'ResidentBroadcastId',
                    "RegistrationId" = "Data" ->> 'RegistrationId',
                    "CompletedAt" = ("Data" ->> 'completedAt')::timestamptz,
                    "Disposition" = "Data" ->> 'disposition',
                    "LegacyExpiresAt" = to_timestamp(("Data" ->> 'expiresAt')::bigint);
                """);

            migrationBuilder.DropColumn(
                name: "Data",
                table: "notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Data", table: "notifications", type: "jsonb", nullable: true);

            migrationBuilder.Sql("""
                UPDATE notifications AS n
                SET "Data" = CASE n."Kind"
                    WHEN 'LOCAL_PENDING' THEN jsonb_build_object(
                        'Id', substr(n."Key", length('LOCAL#') + 1), 'ComplexId', c."ExternalId",
                        'OwnerId', n."OwnerId", 'RequestId', n."RequestId", 'RequestVersion', n."RequestVersion",
                        'Text', n."Text", 'CreatedAt', n."CreatedAt", 'AdministratorEvent', n."AdministratorEvent",
                        'AdministratorId', n."AdministratorId", 'ResidentBroadcastId', n."ResidentBroadcastId", 'RegistrationId', n."RegistrationId")
                    WHEN 'LOCAL_MESSAGE' THEN jsonb_build_object('requestId', n."RequestId")
                    WHEN 'MESSAGE' THEN jsonb_strip_nulls(jsonb_build_object(
                        'pk', split_part(n."Key", chr(31), 1), 'sk', split_part(n."Key", chr(31), 2), 'requestId', n."RequestId"))
                    WHEN 'RESULT' THEN jsonb_strip_nulls(jsonb_build_object(
                        'pk', split_part(n."Key", chr(31), 1), 'sk', split_part(n."Key", chr(31), 2),
                        'completedAt', n."CompletedAt", 'disposition', n."Disposition", 'expiresAt', extract(epoch FROM n."LegacyExpiresAt")::bigint))
                    ELSE jsonb_strip_nulls(jsonb_build_object('completedAt', n."CompletedAt", 'disposition', n."Disposition"))
                END
                FROM complexes AS c
                WHERE c."SourceId" = n."SourceId" AND c."Id" = n."ComplexId";
                """);

            migrationBuilder.AlterColumn<string>(name: "Data", table: "notifications", type: "jsonb", nullable: false,
                oldClrType: typeof(string), oldType: "jsonb", oldNullable: true);

            migrationBuilder.DropColumn(
                name: "AdministratorEvent",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "AdministratorId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "LegacyExpiresAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "RegistrationId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "RequestVersion",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "ResidentBroadcastId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Text",
                table: "notifications");
        }
    }
}
