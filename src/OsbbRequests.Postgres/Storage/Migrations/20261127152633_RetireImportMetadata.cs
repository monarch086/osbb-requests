using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RetireImportMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_administrator_events_import_sources_SourceId",
                table: "administrator_events");

            migrationBuilder.DropForeignKey(
                name: "FK_administrator_notification_settings_import_sources_SourceId",
                table: "administrator_notification_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_administrator_permissions_import_sources_SourceId",
                table: "administrator_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_administrators_import_sources_SourceId",
                table: "administrators");

            migrationBuilder.DropForeignKey(
                name: "FK_api_daily_statistics_import_sources_SourceId",
                table: "api_daily_statistics");

            migrationBuilder.DropForeignKey(
                name: "FK_broadcast_messages_import_sources_SourceId",
                table: "broadcast_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_complexes_import_sources_SourceId",
                table: "complexes");

            migrationBuilder.DropForeignKey(
                name: "FK_notifications_import_sources_SourceId",
                table: "notifications");

            migrationBuilder.DropForeignKey(
                name: "FK_request_events_import_sources_SourceId",
                table: "request_events");

            migrationBuilder.DropForeignKey(
                name: "FK_request_key_items_import_sources_SourceId",
                table: "request_key_items");

            migrationBuilder.DropForeignKey(
                name: "FK_requests_import_sources_SourceId",
                table: "requests");

            migrationBuilder.DropForeignKey(
                name: "FK_resident_events_import_sources_SourceId",
                table: "resident_events");

            migrationBuilder.DropForeignKey(
                name: "FK_resident_membership_history_import_sources_SourceId",
                table: "resident_membership_history");

            migrationBuilder.DropForeignKey(
                name: "FK_resident_memberships_import_sources_SourceId",
                table: "resident_memberships");

            migrationBuilder.DropForeignKey(
                name: "FK_resident_registration_snapshots_import_sources_SourceId",
                table: "resident_registration_snapshots");

            migrationBuilder.DropTable(
                name: "import_runs");

            migrationBuilder.DropTable(
                name: "import_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_sources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AwsAccountId = table.Column<string>(type: "text", nullable: true),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Stage = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_sources", x => x.Id);
                });

            migrationBuilder.Sql("""
                INSERT INTO import_sources ("Id", "AwsAccountId", "Region", "Stage")
                SELECT DISTINCT source_id, NULL, 'legacy', 'legacy-' || source_id::text
                FROM (
                    SELECT "SourceId" AS source_id FROM administrator_events
                    UNION SELECT "SourceId" FROM administrator_notification_settings
                    UNION SELECT "SourceId" FROM administrator_permissions
                    UNION SELECT "SourceId" FROM administrators
                    UNION SELECT "SourceId" FROM api_daily_statistics
                    UNION SELECT "SourceId" FROM broadcast_messages
                    UNION SELECT "SourceId" FROM complexes
                    UNION SELECT "SourceId" FROM notifications
                    UNION SELECT "SourceId" FROM request_events
                    UNION SELECT "SourceId" FROM request_key_items
                    UNION SELECT "SourceId" FROM requests
                    UNION SELECT "SourceId" FROM resident_events
                    UNION SELECT "SourceId" FROM resident_membership_history
                    UNION SELECT "SourceId" FROM resident_memberships
                    UNION SELECT "SourceId" FROM resident_registration_snapshots
                ) AS source_ids
                ON CONFLICT ("Id") DO NOTHING;
                SELECT setval(pg_get_serial_sequence('import_sources', 'Id'),
                              coalesce((SELECT max("Id") FROM import_sources), 1), true);
                """);

            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedCount = table.Column<long>(type: "bigint", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReadCount = table.Column<long>(type: "bigint", nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_runs_import_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "import_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_import_runs_SourceId",
                table: "import_runs",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_import_sources_AwsAccountId_Region_Stage",
                table: "import_sources",
                columns: new[] { "AwsAccountId", "Region", "Stage" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_administrator_events_import_sources_SourceId",
                table: "administrator_events",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_administrator_notification_settings_import_sources_SourceId",
                table: "administrator_notification_settings",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_administrator_permissions_import_sources_SourceId",
                table: "administrator_permissions",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_administrators_import_sources_SourceId",
                table: "administrators",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_api_daily_statistics_import_sources_SourceId",
                table: "api_daily_statistics",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_broadcast_messages_import_sources_SourceId",
                table: "broadcast_messages",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_complexes_import_sources_SourceId",
                table: "complexes",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notifications_import_sources_SourceId",
                table: "notifications",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_request_events_import_sources_SourceId",
                table: "request_events",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_request_key_items_import_sources_SourceId",
                table: "request_key_items",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_requests_import_sources_SourceId",
                table: "requests",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_resident_events_import_sources_SourceId",
                table: "resident_events",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_resident_membership_history_import_sources_SourceId",
                table: "resident_membership_history",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_resident_memberships_import_sources_SourceId",
                table: "resident_memberships",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_resident_registration_snapshots_import_sources_SourceId",
                table: "resident_registration_snapshots",
                column: "SourceId",
                principalTable: "import_sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
