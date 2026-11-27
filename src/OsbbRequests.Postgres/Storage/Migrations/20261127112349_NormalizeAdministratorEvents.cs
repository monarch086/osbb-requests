using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeAdministratorEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "administrator_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "administrator_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ComplexRequests",
                table: "administrator_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "administrator_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeyOrders",
                table: "administrator_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Messages",
                table: "administrator_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingResidents",
                table: "administrator_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RegisteredResidents",
                table: "administrator_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM administrator_events
                        WHERE jsonb_typeof("Data") <> 'object'
                           OR "Data" - ARRAY['pk', 'sk', 'action', 'actor', 'createdAt', 'permissions'] <> '{}'::jsonb
                           OR ("Data" ->> 'pk') <> split_part("Key", chr(31), 1)
                           OR ("Data" ->> 'sk') <> split_part("Key", chr(31), 2)
                           OR ("Data" ->> 'permissions')::jsonb
                              - ARRAY['ComplexRequests', 'KeyOrders', 'RegisteredResidents', 'PendingResidents', 'Messages'] <> '{}'::jsonb
                    ) THEN
                        RAISE EXCEPTION 'Unmapped data in administrator_events';
                    END IF;
                END $$;

                WITH snapshots AS (
                    SELECT "SourceId", "Key", ("Data" ->> 'permissions')::jsonb AS permissions
                    FROM administrator_events
                )
                UPDATE administrator_events AS audit
                SET "Action" = audit."Data" ->> 'action',
                    "Actor" = audit."Data" ->> 'actor',
                    "CreatedAt" = (audit."Data" ->> 'createdAt')::timestamptz,
                    "ComplexRequests" = (snapshots.permissions ->> 'ComplexRequests')::boolean,
                    "KeyOrders" = (snapshots.permissions ->> 'KeyOrders')::boolean,
                    "RegisteredResidents" = (snapshots.permissions ->> 'RegisteredResidents')::boolean,
                    "PendingResidents" = (snapshots.permissions ->> 'PendingResidents')::boolean,
                    "Messages" = (snapshots.permissions ->> 'Messages')::boolean
                FROM snapshots
                WHERE audit."SourceId" = snapshots."SourceId" AND audit."Key" = snapshots."Key";
                """);

            migrationBuilder.DropColumn(
                name: "Data",
                table: "administrator_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "administrator_events",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE administrator_events
                SET "Data" = jsonb_strip_nulls(jsonb_build_object(
                    'pk', CASE WHEN strpos("Key", chr(31)) > 0 THEN split_part("Key", chr(31), 1) END,
                    'sk', CASE WHEN strpos("Key", chr(31)) > 0 THEN split_part("Key", chr(31), 2) END,
                    'action', "Action", 'actor', "Actor", 'createdAt', "CreatedAt",
                    'permissions', NULLIF(jsonb_strip_nulls(jsonb_build_object(
                        'ComplexRequests', "ComplexRequests", 'KeyOrders', "KeyOrders", 'RegisteredResidents', "RegisteredResidents",
                        'PendingResidents', "PendingResidents", 'Messages', "Messages")), '{}'::jsonb)::text
                ));
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Data",
                table: "administrator_events",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Action",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "Actor",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "ComplexRequests",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "KeyOrders",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "Messages",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "PendingResidents",
                table: "administrator_events");

            migrationBuilder.DropColumn(
                name: "RegisteredResidents",
                table: "administrator_events");
        }
    }
}
