using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Data.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectRequestVersionsAndResidentKeyHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");

            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "resident_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentKeySerialNumber",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentKeyType",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "KeyRequestId",
                table: "resident_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousKeySerialNumber",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousKeyType",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE requests AS r
                SET "Version" = (s."Data"->>'version')::integer
                FROM source_records AS s
                WHERE s."SourceId" = r."SourceId" AND s."Table" = 'command-data'
                  AND s."Key" = 'USER#' || r."OwnerId" || chr(31) || 'REQUEST#' || lpad(r."RequestId"::text, 20, '0')
                  AND pg_input_is_valid(s."Data"->>'version', 'integer');
                """);

            migrationBuilder.Sql("""
                WITH payloads AS (
                    SELECT "SourceId", "Key", "Data",
                           CASE WHEN "EventKey" LIKE 'EVENT#TYPE#%'
                                 AND jsonb_typeof("Data"->'payload') = 'string'
                                 AND pg_input_is_valid("Data"->>'payload', 'jsonb')
                                THEN ("Data"->>'payload')::jsonb END AS body
                    FROM resident_events
                )
                UPDATE resident_events AS e
                SET "Actor" = COALESCE(p.body->>'Actor', p."Data"->>'actor'),
                    "CreatedAt" = COALESCE(
                        CASE WHEN pg_input_is_valid(p.body->>'CreatedAt', 'timestamp with time zone')
                             THEN (p.body->>'CreatedAt')::timestamptz END,
                        CASE WHEN pg_input_is_valid(p."Data"->>'createdAt', 'timestamp with time zone')
                             THEN (p."Data"->>'createdAt')::timestamptz END),
                    "KeyRequestId" = (p.body#>>'{KeyChange,RequestId}')::bigint,
                    "PreviousKeyType" = p.body#>>'{KeyChange,Previous,Type}',
                    "PreviousKeySerialNumber" = p.body#>>'{KeyChange,Previous,SerialNumber}',
                    "CurrentKeyType" = p.body#>>'{KeyChange,Current,Type}',
                    "CurrentKeySerialNumber" = p.body#>>'{KeyChange,Current,SerialNumber}'
                FROM payloads AS p
                WHERE e."SourceId" = p."SourceId" AND e."Key" = p."Key";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");

            migrationBuilder.DropColumn(
                name: "Actor",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "CurrentKeySerialNumber",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "CurrentKeyType",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "KeyRequestId",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "PreviousKeySerialNumber",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "PreviousKeyType",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "requests");
        }
    }
}
