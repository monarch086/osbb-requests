using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Data.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AdministratorSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "administrator_notification_settings",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    TelegramChatId = table.Column<string>(type: "text", nullable: true),
                    NewResidents = table.Column<bool>(type: "boolean", nullable: false),
                    NewRequests = table.Column<bool>(type: "boolean", nullable: false),
                    NewKeyOrders = table.Column<bool>(type: "boolean", nullable: false),
                    NewAdministrators = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator_notification_settings", x => new { x.Source, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "administrator_permissions",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ComplexRequests = table.Column<bool>(type: "boolean", nullable: false),
                    KeyOrders = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredResidents = table.Column<bool>(type: "boolean", nullable: false),
                    PendingResidents = table.Column<bool>(type: "boolean", nullable: false),
                    Messages = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator_permissions", x => new { x.Source, x.UserId });
                });

            migrationBuilder.Sql("UPDATE source_records SET \"ProjectionVersion\" = 0 WHERE \"Table\" = 'administrators'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "administrator_notification_settings");

            migrationBuilder.DropTable(
                name: "administrator_permissions");
        }
    }
}
