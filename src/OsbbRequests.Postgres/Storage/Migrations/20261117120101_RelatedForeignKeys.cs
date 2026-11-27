using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.DataImport.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RelatedForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_administrator_notification_settings_administrators_Source_U~",
                table: "administrator_notification_settings",
                columns: new[] { "Source", "UserId" },
                principalTable: "administrators",
                principalColumns: new[] { "Source", "UserId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_administrator_permissions_administrators_Source_UserId",
                table: "administrator_permissions",
                columns: new[] { "Source", "UserId" },
                principalTable: "administrators",
                principalColumns: new[] { "Source", "UserId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_request_key_items_requests_Source_OwnerId_RequestId",
                table: "request_key_items",
                columns: new[] { "Source", "OwnerId", "RequestId" },
                principalTable: "requests",
                principalColumns: new[] { "Source", "OwnerId", "RequestId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_resident_consents_residents_Source_ComplexId_TelegramUserId",
                table: "resident_consents",
                columns: new[] { "Source", "ComplexId", "TelegramUserId" },
                principalTable: "residents",
                principalColumns: new[] { "Source", "ComplexId", "TelegramUserId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_resident_phones_residents_Source_ComplexId_TelegramUserId",
                table: "resident_phones",
                columns: new[] { "Source", "ComplexId", "TelegramUserId" },
                principalTable: "residents",
                principalColumns: new[] { "Source", "ComplexId", "TelegramUserId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_administrator_notification_settings_administrators_Source_U~",
                table: "administrator_notification_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_administrator_permissions_administrators_Source_UserId",
                table: "administrator_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_request_key_items_requests_Source_OwnerId_RequestId",
                table: "request_key_items");

            migrationBuilder.DropForeignKey(
                name: "FK_resident_consents_residents_Source_ComplexId_TelegramUserId",
                table: "resident_consents");

            migrationBuilder.DropForeignKey(
                name: "FK_resident_phones_residents_Source_ComplexId_TelegramUserId",
                table: "resident_phones");
        }
    }
}
