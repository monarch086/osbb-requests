using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DropEmptySourceRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM source_records LIMIT 1) THEN
                        RAISE EXCEPTION 'source_records must be empty before removal';
                    END IF;
                END
                $migration$;
                """);
            migrationBuilder.DropTable(name: "source_records");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_records",
                columns: table => new
                {
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    Table = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProjectionVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_records", x => new { x.SourceId, x.Table, x.Key });
                    table.ForeignKey(
                        name: "FK_source_records_import_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "import_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }
    }
}
