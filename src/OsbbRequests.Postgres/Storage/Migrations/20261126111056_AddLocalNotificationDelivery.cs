using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalNotificationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AudienceAfter",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lease",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_ComplexId_DueAt",
                table: "notifications",
                columns: new[] { "ComplexId", "DueAt" },
                filter: "\"Kind\" = 'LOCAL_PENDING'");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_ComplexId_ExpiresAt",
                table: "notifications",
                columns: new[] { "ComplexId", "ExpiresAt" },
                filter: "\"ExpiresAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_ComplexId_DueAt",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_ComplexId_ExpiresAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "AudienceAfter",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "DueAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "Lease",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "notifications");
        }
    }
}
