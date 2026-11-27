using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace OsbbRequests.DataImport.Storage.Migrations;

public partial class NormalizeImportSources : Migration
{
    private static readonly string[] SourceTables =
    [
        "administrators", "administrator_events", "administrator_notification_settings", "administrator_permissions",
        "api_daily_statistics", "broadcast_messages", "import_runs", "notifications", "requests", "request_events",
        "request_key_items", "residents", "resident_apartments", "resident_consents", "resident_events",
        "resident_memberships", "resident_phones", "source_records"
    ];

    private static readonly (string Table, string Principal, string[] Key, string OldName, string NewName)[] CompositeForeignKeys =
    [
        ("administrator_notification_settings", "administrators", ["UserId"],
            "FK_administrator_notification_settings_administrators_Source_U~",
            "FK_administrator_notification_settings_administrators_SourceId~"),
        ("administrator_permissions", "administrators", ["UserId"],
            "FK_administrator_permissions_administrators_Source_UserId",
            "FK_administrator_permissions_administrators_SourceId_UserId"),
        ("request_key_items", "requests", ["OwnerId", "RequestId"],
            "FK_request_key_items_requests_Source_OwnerId_RequestId",
            "FK_request_key_items_requests_SourceId_OwnerId_RequestId"),
        ("resident_consents", "residents", ["ComplexId", "TelegramUserId"],
            "FK_resident_consents_residents_Source_ComplexId_TelegramUserId",
            "FK_resident_consents_residents_SourceId_ComplexId_TelegramUser~"),
        ("resident_phones", "residents", ["ComplexId", "TelegramUserId"],
            "FK_resident_phones_residents_Source_ComplexId_TelegramUserId",
            "FK_resident_phones_residents_SourceId_ComplexId_TelegramUserId")
    ];

    private static readonly (string Table, string OldName, string NewName)[] SourceIndexes =
    [
        ("resident_events", "IX_resident_events_Source_RegistrationId", "IX_resident_events_SourceId_RegistrationId"),
        ("resident_apartments", "IX_resident_apartments_Source_ComplexId_BuildingNumber_Apartme~",
            "IX_resident_apartments_SourceId_ComplexId_BuildingNumber_Apart~"),
        ("requests", "IX_requests_Source_ComplexId_Status_CreatedAt", "IX_requests_SourceId_ComplexId_Status_CreatedAt"),
        ("request_events", "IX_request_events_Source_RequestId_CreatedAt", "IX_request_events_SourceId_RequestId_CreatedAt"),
        ("broadcast_messages", "IX_broadcast_messages_Source_ComplexId_CreatedAt", "IX_broadcast_messages_SourceId_ComplexId_CreatedAt")
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");

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
            constraints: table => table.PrimaryKey("PK_import_sources", row => row.Id));

        migrationBuilder.CreateIndex(
            name: "IX_import_sources_AwsAccountId_Region_Stage",
            table: "import_sources",
            columns: ["AwsAccountId", "Region", "Stage"],
            unique: true)
            .Annotation("Npgsql:NullsDistinct", false);

        var legacySources = string.Join("\nUNION\n", SourceTables.Select(table => $"""SELECT DISTINCT "Source" FROM {table}"""));
        migrationBuilder.Sql($$"""
            INSERT INTO import_sources ("AwsAccountId", "Region", "Stage")
            SELECT NULLIF(split_part("Source", ':', 1), 'unknown'),
                   split_part("Source", ':', 2), split_part("Source", ':', 3)
            FROM (
                {{legacySources}}
            ) AS legacy
            WHERE "Source" ~ '^(unknown|[0-9]{12}):[^:]+:[^:]+$';
            """);

        migrationBuilder.Sql("""
            CREATE FUNCTION pg_temp.normalized_import_source(legacy text) RETURNS bigint
            LANGUAGE sql STABLE STRICT AS $lookup$
                SELECT "Id" FROM import_sources
                WHERE "AwsAccountId" IS NOT DISTINCT FROM NULLIF(split_part(legacy, ':', 1), 'unknown')
                  AND "Region" = split_part(legacy, ':', 2)
                  AND "Stage" = split_part(legacy, ':', 3)
                  AND legacy ~ '^(unknown|[0-9]{12}):[^:]+:[^:]+$'
            $lookup$;
            """);

        foreach (var foreignKey in CompositeForeignKeys)
            migrationBuilder.DropForeignKey(foreignKey.OldName, foreignKey.Table);

        // PostgreSQL rebuilds indexes for the new type and keeps primary-key definitions.
        foreach (var table in SourceTables)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE {table} ALTER COLUMN "Source" TYPE bigint USING pg_temp.normalized_import_source("Source");
                """);
            migrationBuilder.RenameColumn("Source", table, "SourceId");
            migrationBuilder.AddForeignKey($"FK_{table}_import_sources_SourceId", table, "SourceId",
                "import_sources", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        migrationBuilder.Sql("DROP FUNCTION pg_temp.normalized_import_source(text);");
        foreach (var index in SourceIndexes)
            migrationBuilder.RenameIndex(name: index.OldName, table: index.Table, newName: index.NewName);
        migrationBuilder.CreateIndex("IX_import_runs_SourceId", "import_runs", "SourceId");

        foreach (var foreignKey in CompositeForeignKeys)
        {
            string[] columns = ["SourceId", .. foreignKey.Key];
            migrationBuilder.AddForeignKey(foreignKey.NewName, foreignKey.Table, columns,
                foreignKey.Principal, principalColumns: columns, onDelete: ReferentialAction.Cascade);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");
        foreach (var foreignKey in CompositeForeignKeys)
            migrationBuilder.DropForeignKey(foreignKey.NewName, foreignKey.Table);
        foreach (var index in SourceIndexes)
            migrationBuilder.RenameIndex(name: index.NewName, table: index.Table, newName: index.OldName);
        migrationBuilder.DropIndex("IX_import_runs_SourceId", "import_runs");

        migrationBuilder.Sql("""
            CREATE FUNCTION pg_temp.legacy_import_source(source_id bigint) RETURNS text
            LANGUAGE sql STABLE STRICT AS $lookup$
                SELECT COALESCE("AwsAccountId", 'unknown') || ':' || "Region" || ':' || "Stage"
                FROM import_sources WHERE "Id" = source_id
            $lookup$;
            """);

        foreach (var table in SourceTables)
        {
            migrationBuilder.DropForeignKey($"FK_{table}_import_sources_SourceId", table);
            migrationBuilder.Sql($"""
                ALTER TABLE {table} ALTER COLUMN "SourceId" TYPE text USING pg_temp.legacy_import_source("SourceId");
                """);
            migrationBuilder.RenameColumn("SourceId", table, "Source");
        }

        migrationBuilder.Sql("DROP FUNCTION pg_temp.legacy_import_source(bigint);");
        migrationBuilder.DropTable("import_sources");
        foreach (var foreignKey in CompositeForeignKeys)
        {
            string[] columns = ["Source", .. foreignKey.Key];
            migrationBuilder.AddForeignKey(foreignKey.OldName, foreignKey.Table, columns,
                foreignKey.Principal, principalColumns: columns, onDelete: ReferentialAction.Cascade);
        }
    }
}
