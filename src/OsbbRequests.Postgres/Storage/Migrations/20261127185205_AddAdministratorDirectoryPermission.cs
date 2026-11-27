using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministratorDirectoryPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Administrators",
                table: "administrator_permissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Administrators",
                table: "administrator_events",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Administrators",
                table: "administrator_permissions");

            migrationBuilder.DropColumn(
                name: "Administrators",
                table: "administrator_events");
        }
    }
}
