using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectAdministratorSuperAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuperAdmin",
                table: "administrators",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE administrators AS admin
                SET "IsSuperAdmin" = TRUE
                FROM source_records AS source
                WHERE source."SourceId" = admin."SourceId"
                  AND source."Table" = 'administrators'
                  AND source."Key" = admin."UserId"
                  AND source."Data" -> 'IsSuperAdmin' = 'true'::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSuperAdmin",
                table: "administrators");
        }
    }
}
