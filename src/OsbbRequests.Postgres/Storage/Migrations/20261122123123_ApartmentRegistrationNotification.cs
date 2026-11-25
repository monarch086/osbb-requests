using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.DataImport.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ApartmentRegistrationNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NewApartmentRegistrations",
                table: "administrator_notification_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql("""
                UPDATE administrator_notification_settings
                SET "NewApartmentRegistrations" = "NewResidents"
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewApartmentRegistrations",
                table: "administrator_notification_settings");
        }
    }
}
