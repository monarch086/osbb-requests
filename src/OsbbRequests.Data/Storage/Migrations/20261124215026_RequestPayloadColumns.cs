using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Data.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RequestPayloadColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedTo",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildingNumber",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Entrance",
                table: "requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Floor",
                table: "requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeyQuantity",
                table: "requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyType",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PlannedDate",
                table: "requests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "requests",
                type: "text",
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "requests",
                type: "text",
                nullable: false,
                defaultValue: "Private");

            migrationBuilder.Sql("""
                UPDATE requests
                SET "AssignedTo" = "Payload"->>'AssignedTo',
                    "BuildingNumber" = "Payload"->>'BuildingNumber',
                    "Category" = "Payload"->>'Category',
                    "Description" = "Payload"->>'Description',
                    "Entrance" = ("Payload"->>'Entrance')::integer,
                    "Floor" = ("Payload"->>'Floor')::integer,
                    "KeyQuantity" = ("Payload"->>'KeyQuantity')::integer,
                    "KeyType" = "Payload"->>'KeyType',
                    "Location" = "Payload"->>'Location',
                    "PlannedDate" = ("Payload"->>'PlannedDate')::date,
                    "Priority" = COALESCE("Payload"->>'Priority', 'Normal'),
                    "Resolution" = "Payload"->>'Resolution',
                    "Scope" = CASE "Payload"->>'Scope'
                        WHEN '0' THEN 'Complex'
                        WHEN '1' THEN 'Building'
                        ELSE "Payload"->>'Scope'
                    END,
                    "Status" = CASE "Status"
                        WHEN '0' THEN 'New'
                        WHEN '1' THEN 'InProgress'
                        WHEN '2' THEN 'AwaitingClarification'
                        WHEN '3' THEN 'Completed'
                        WHEN '4' THEN 'Rejected'
                        ELSE "Status"
                    END,
                    "Visibility" = CASE "Payload"->>'Visibility'
                        WHEN '0' THEN 'Private'
                        WHEN '1' THEN 'Public'
                        ELSE COALESCE("Payload"->>'Visibility', 'Private')
                    END;

                INSERT INTO request_key_items ("Source", "OwnerId", "RequestId", "Position", "Type", "Phone", "SerialNumber")
                SELECT r."Source", r."OwnerId", r."RequestId", (item.ordinality - 1)::integer,
                       COALESCE(item.value->>'Type', ''), COALESCE(item.value->>'Phone', ''), item.value->>'SerialNumber'
                FROM requests AS r
                CROSS JOIN LATERAL jsonb_array_elements(
                    CASE WHEN jsonb_typeof(r."Payload"->'KeyItems') = 'array' THEN r."Payload"->'KeyItems' ELSE '[]'::jsonb END
                ) WITH ORDINALITY AS item(value, ordinality)
                ON CONFLICT ("Source", "OwnerId", "RequestId", "Position") DO NOTHING;
                """);

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "requests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "requests",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.Sql("""
                UPDATE requests
                SET "Status" = CASE "Status"
                    WHEN 'New' THEN '0'
                    WHEN 'InProgress' THEN '1'
                    WHEN 'AwaitingClarification' THEN '2'
                    WHEN 'Completed' THEN '3'
                    WHEN 'Rejected' THEN '4'
                    ELSE "Status"
                END;

                UPDATE requests AS r
                SET "Payload" = jsonb_strip_nulls(jsonb_build_object(
                    'Id', r."RequestId",
                    'OwnerId', r."OwnerId",
                    'BuildingId', r."ComplexId",
                    'Category', r."Category",
                    'Location', r."Location",
                    'Description', r."Description",
                    'CreatedAt', r."CreatedAt",
                    'Status', CASE WHEN r."Status" ~ '^-?[0-9]+$' THEN to_jsonb(r."Status"::integer) ELSE to_jsonb(r."Status") END,
                    'Visibility', CASE r."Visibility" WHEN 'Private' THEN '0'::jsonb WHEN 'Public' THEN '1'::jsonb ELSE to_jsonb(r."Visibility") END,
                    'Resolution', r."Resolution",
                    'AssignedTo', r."AssignedTo",
                    'Priority', r."Priority",
                    'PlannedDate', r."PlannedDate",
                    'Scope', r."Scope",
                    'BuildingNumber', r."BuildingNumber",
                    'Floor', r."Floor",
                    'Entrance', r."Entrance",
                    'KeyType', r."KeyType",
                    'KeyQuantity', r."KeyQuantity",
                    'KeyItems', (
                        SELECT jsonb_agg(jsonb_build_object('Type', k."Type", 'Phone', k."Phone", 'SerialNumber', k."SerialNumber")
                                         ORDER BY k."Position")
                        FROM request_key_items AS k
                        WHERE k."Source" = r."Source" AND k."OwnerId" = r."OwnerId" AND k."RequestId" = r."RequestId"
                    )
                ));
                """);

            migrationBuilder.DropColumn(
                name: "AssignedTo",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "BuildingNumber",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Entrance",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Floor",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "KeyQuantity",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "KeyType",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "PlannedDate",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "requests");
        }
    }
}
