using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeResidentAndRequestEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalMode",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Decision",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FirstNameChanged",
                table: "resident_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastNameChanged",
                table: "resident_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NewType",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousType",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "resident_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "request_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Actor",
                table: "request_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ActorId",
                table: "request_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Entrance",
                table: "request_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Floor",
                table: "request_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasEntrance",
                table: "request_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasFloor",
                table: "request_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasResolution",
                table: "request_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreviousStatus",
                table: "request_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                table: "request_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "request_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Target",
                table: "request_events",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "request_event_snapshots",
                columns: table => new
                {
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    AssignedTo = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<string>(type: "text", nullable: true),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: true),
                    BuildingNumber = table.Column<string>(type: "text", nullable: true),
                    Floor = table.Column<int>(type: "integer", nullable: true),
                    Entrance = table.Column<int>(type: "integer", nullable: true),
                    HasEntrance = table.Column<bool>(type: "boolean", nullable: false),
                    KeySerialNumbersChanged = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_event_snapshots", x => new { x.SourceId, x.Key });
                    table.ForeignKey(
                        name: "FK_request_event_snapshots_request_events_SourceId_Key",
                        columns: x => new { x.SourceId, x.Key },
                        principalTable: "request_events",
                        principalColumns: new[] { "SourceId", "Key" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM resident_events
                        WHERE "Data" - ARRAY['pk','sk','actor','createdAt','decision','reason','approvalMode','payload',
                                            'Actor','CreatedAt','PreviousType','NewType','FirstNameChanged','LastNameChanged','KeyChange'] <> '{}'::jsonb
                    ) OR EXISTS (
                        SELECT 1
                        FROM resident_events
                        CROSS JOIN LATERAL (SELECT ("Data" ->> 'payload')::jsonb AS payload) AS change
                        WHERE payload - ARRAY['Actor','CreatedAt','PreviousType','NewType','FirstNameChanged','LastNameChanged','KeyChange'] <> '{}'::jsonb
                    ) OR EXISTS (
                        SELECT 1
                        FROM resident_events
                        CROSS JOIN LATERAL (SELECT COALESCE(("Data" ->> 'payload')::jsonb, "Data") -> 'KeyChange' AS details) AS change
                        WHERE CASE WHEN jsonb_typeof(details) = 'object' THEN
                            details - ARRAY['RequestId','Previous','Current'] <> '{}'::jsonb
                            OR (details -> 'Previous') - ARRAY['Type','SerialNumber'] <> '{}'::jsonb
                            OR CASE WHEN jsonb_typeof(details -> 'Current') = 'object'
                                    THEN (details -> 'Current') - ARRAY['Type','SerialNumber'] <> '{}'::jsonb END
                        END
                    ) THEN
                        RAISE EXCEPTION 'Unmapped data in resident_events';
                    END IF;

                    IF EXISTS (
                        SELECT 1 FROM request_events
                        WHERE "Data" - ARRAY['pk','sk','action','actor','actorId','createdAt','status','previousStatus',
                                            'resolution','target','floor','Floor','entrance','Entrance','changes'] <> '{}'::jsonb
                           OR ("Data" ? 'floor' AND "Data" ? 'Floor') OR ("Data" ? 'entrance' AND "Data" ? 'Entrance')
                    ) OR EXISTS (
                        SELECT 1
                        FROM request_events
                        CROSS JOIN LATERAL (SELECT ("Data" ->> 'changes')::jsonb AS snapshot) AS changes
                        WHERE snapshot - ARRAY['AssignedTo','Priority','PlannedDate','Visibility','Scope','BuildingNumber',
                                               'Floor','Entrance','KeySerialNumbersChanged'] <> '{}'::jsonb
                           OR NOT (snapshot ?& ARRAY['AssignedTo','Priority','PlannedDate','Visibility','Scope','BuildingNumber','Floor'])
                    ) THEN
                        RAISE EXCEPTION 'Unmapped data in request_events';
                    END IF;
                END $$;

                WITH payloads AS (
                    SELECT "SourceId", "Key", COALESCE(("Data" ->> 'payload')::jsonb, "Data") AS payload
                    FROM resident_events
                )
                UPDATE resident_events AS event
                SET "Decision" = event."Data" ->> 'decision',
                    "Reason" = event."Data" ->> 'reason',
                    "ApprovalMode" = event."Data" ->> 'approvalMode',
                    "Actor" = COALESCE(payload ->> 'Actor', event."Actor", event."Data" ->> 'actor'),
                    "CreatedAt" = COALESCE((payload ->> 'CreatedAt')::timestamptz, event."CreatedAt", (event."Data" ->> 'createdAt')::timestamptz),
                    "PreviousType" = payload ->> 'PreviousType',
                    "NewType" = payload ->> 'NewType',
                    "FirstNameChanged" = (payload ->> 'FirstNameChanged')::boolean,
                    "LastNameChanged" = (payload ->> 'LastNameChanged')::boolean,
                    "KeyRequestId" = COALESCE((payload #>> '{KeyChange,RequestId}')::bigint, event."KeyRequestId"),
                    "PreviousKeyType" = COALESCE(payload #>> '{KeyChange,Previous,Type}', event."PreviousKeyType"),
                    "PreviousKeySerialNumber" = COALESCE(payload #>> '{KeyChange,Previous,SerialNumber}', event."PreviousKeySerialNumber"),
                    "CurrentKeyType" = COALESCE(payload #>> '{KeyChange,Current,Type}', event."CurrentKeyType"),
                    "CurrentKeySerialNumber" = COALESCE(payload #>> '{KeyChange,Current,SerialNumber}', event."CurrentKeySerialNumber")
                FROM payloads
                WHERE event."SourceId" = payloads."SourceId" AND event."Key" = payloads."Key";

                UPDATE request_events
                SET "Action" = "Data" ->> 'action',
                    "Actor" = "Data" ->> 'actor',
                    "ActorId" = ("Data" ->> 'actorId')::bigint,
                    "CreatedAt" = COALESCE("CreatedAt", ("Data" ->> 'createdAt')::timestamptz),
                    "Status" = "Data" ->> 'status',
                    "PreviousStatus" = "Data" ->> 'previousStatus',
                    "Resolution" = "Data" ->> 'resolution',
                    "HasResolution" = "Data" ? 'resolution',
                    "Target" = "Data" ->> 'target',
                    "Floor" = COALESCE("Data" ->> 'floor', "Data" ->> 'Floor')::integer,
                    "HasFloor" = "Data" ?| ARRAY['floor','Floor'],
                    "Entrance" = COALESCE("Data" ->> 'entrance', "Data" ->> 'Entrance')::integer,
                    "HasEntrance" = "Data" ?| ARRAY['entrance','Entrance'];

                INSERT INTO request_event_snapshots (
                    "SourceId", "Key", "AssignedTo", "Priority", "PlannedDate", "Visibility", "Scope",
                    "BuildingNumber", "Floor", "Entrance", "HasEntrance", "KeySerialNumbersChanged")
                SELECT "SourceId", "Key", snapshot ->> 'AssignedTo', snapshot ->> 'Priority',
                       (snapshot ->> 'PlannedDate')::date, (snapshot ->> 'Visibility')::integer, snapshot ->> 'Scope',
                       snapshot ->> 'BuildingNumber', (snapshot ->> 'Floor')::integer,
                       (snapshot ->> 'Entrance')::integer, snapshot ? 'Entrance', (snapshot ->> 'KeySerialNumbersChanged')::boolean
                FROM request_events
                CROSS JOIN LATERAL (SELECT ("Data" ->> 'changes')::jsonb AS snapshot) AS changes
                WHERE snapshot IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "Data",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "Data",
                table: "request_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Data", table: "resident_events", type: "jsonb", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Data", table: "request_events", type: "jsonb", nullable: true);

            migrationBuilder.Sql("""
                WITH changes AS (
                    SELECT "SourceId", "Key", jsonb_build_object(
                        'PreviousType', "PreviousType", 'NewType', "NewType", 'Actor', "Actor", 'CreatedAt', "CreatedAt",
                        'FirstNameChanged', COALESCE("FirstNameChanged", false), 'LastNameChanged', COALESCE("LastNameChanged", false),
                        'KeyChange', CASE WHEN "KeyRequestId" IS NOT NULL THEN jsonb_build_object(
                            'RequestId', "KeyRequestId", 'Previous', jsonb_build_object('Type',"PreviousKeyType",'SerialNumber',"PreviousKeySerialNumber"),
                            'Current', CASE WHEN "CurrentKeyType" IS NOT NULL THEN
                                jsonb_build_object('Type',"CurrentKeyType",'SerialNumber',"CurrentKeySerialNumber") END) END) AS payload
                    FROM resident_events
                )
                UPDATE resident_events AS event
                SET "Data" = CASE
                    WHEN "EventKey" LIKE 'EVENT#TYPE#%' AND event."Key" LIKE 'LOCAL#%' THEN changes.payload
                    WHEN "EventKey" LIKE 'EVENT#TYPE#%' THEN jsonb_build_object('payload', changes.payload::text)
                    ELSE jsonb_strip_nulls(jsonb_build_object('actor',"Actor",'createdAt',"CreatedAt",
                                                             'decision',"Decision",'reason',"Reason",'approvalMode',"ApprovalMode"))
                END || CASE WHEN strpos(event."Key", chr(31)) > 0 THEN jsonb_build_object(
                    'pk', 'REGISTRATION#' || "RegistrationId", 'sk', "EventKey")
                    ELSE '{}'::jsonb END
                FROM changes
                WHERE event."SourceId" = changes."SourceId" AND event."Key" = changes."Key";

                UPDATE request_events AS event
                SET "Data" = jsonb_strip_nulls(jsonb_build_object(
                    'action',"Action",'actor',"Actor",'actorId',"ActorId",'createdAt',"CreatedAt",
                    'status',"Status",'previousStatus',"PreviousStatus",'target',"Target"))
                    || CASE WHEN "HasResolution" THEN jsonb_build_object('resolution',"Resolution") ELSE '{}'::jsonb END
                    || CASE WHEN "HasFloor" THEN jsonb_build_object(CASE WHEN "Key" LIKE 'LOCAL#%' THEN 'Floor' ELSE 'floor' END, "Floor") ELSE '{}'::jsonb END
                    || CASE WHEN "HasEntrance" THEN jsonb_build_object(CASE WHEN "Key" LIKE 'LOCAL#%' THEN 'Entrance' ELSE 'entrance' END, "Entrance") ELSE '{}'::jsonb END
                    || CASE WHEN strpos("Key", chr(31)) > 0 THEN jsonb_build_object(
                        'pk',split_part("Key",chr(31),1),'sk',split_part("Key",chr(31),2)) ELSE '{}'::jsonb END;

                UPDATE request_events AS event
                SET "Data" = event."Data" || jsonb_build_object('changes', (
                    jsonb_build_object('AssignedTo',s."AssignedTo",'Priority',s."Priority",'PlannedDate',s."PlannedDate",
                                       'Visibility',s."Visibility",'Scope',s."Scope",'BuildingNumber',s."BuildingNumber",'Floor',s."Floor")
                    || CASE WHEN s."HasEntrance" THEN jsonb_build_object('Entrance',s."Entrance") ELSE '{}'::jsonb END
                    || CASE WHEN s."KeySerialNumbersChanged" IS NOT NULL THEN
                        jsonb_build_object('KeySerialNumbersChanged',s."KeySerialNumbersChanged") ELSE '{}'::jsonb END)::text)
                FROM request_event_snapshots AS s
                WHERE event."SourceId" = s."SourceId" AND event."Key" = s."Key";
                """);

            migrationBuilder.AlterColumn<string>(name: "Data", table: "resident_events", type: "jsonb", nullable: false,
                oldClrType: typeof(string), oldType: "jsonb", oldNullable: true);
            migrationBuilder.AlterColumn<string>(name: "Data", table: "request_events", type: "jsonb", nullable: false,
                oldClrType: typeof(string), oldType: "jsonb", oldNullable: true);

            migrationBuilder.DropTable(
                name: "request_event_snapshots");

            migrationBuilder.DropColumn(
                name: "ApprovalMode",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "FirstNameChanged",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "LastNameChanged",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "NewType",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "PreviousType",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "resident_events");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "Actor",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "ActorId",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "Entrance",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "Floor",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "HasEntrance",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "HasFloor",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "HasResolution",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "PreviousStatus",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "request_events");

            migrationBuilder.DropColumn(
                name: "Target",
                table: "request_events");
        }
    }
}
