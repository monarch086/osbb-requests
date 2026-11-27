using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.DataImport.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RelatedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectionVersion",
                table: "source_records",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "administrator_events",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    AdministratorId = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator_events", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "api_daily_statistics",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<string>(type: "text", nullable: false),
                    Endpoint = table.Column<string>(type: "text", nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    ReadUnits = table.Column<double>(type: "double precision", nullable: false),
                    WriteUnits = table.Column<double>(type: "double precision", nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_daily_statistics", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "broadcast_messages",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CreatedById = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broadcast_messages", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "request_events",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    EventKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_events", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "request_key_items",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false),
                    SerialNumber = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_key_items", x => new { x.Source, x.OwnerId, x.RequestId, x.Position });
                });

            migrationBuilder.CreateTable(
                name: "resident_consents",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    ConsentedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_consents", x => new { x.Source, x.ComplexId, x.TelegramUserId });
                });

            migrationBuilder.CreateTable(
                name: "resident_events",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    RegistrationId = table.Column<string>(type: "text", nullable: false),
                    EventKey = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_events", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "resident_memberships",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_memberships", x => new { x.Source, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "resident_phones",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_phones", x => new { x.Source, x.ComplexId, x.TelegramUserId, x.Phone });
                });

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_messages_Source_ComplexId_CreatedAt",
                table: "broadcast_messages",
                columns: new[] { "Source", "ComplexId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_request_events_Source_RequestId_CreatedAt",
                table: "request_events",
                columns: new[] { "Source", "RequestId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_request_key_items_Phone",
                table: "request_key_items",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_resident_events_Source_RegistrationId",
                table: "resident_events",
                columns: new[] { "Source", "RegistrationId" });

            migrationBuilder.CreateIndex(
                name: "IX_resident_phones_Phone",
                table: "resident_phones",
                column: "Phone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "administrator_events");

            migrationBuilder.DropTable(
                name: "api_daily_statistics");

            migrationBuilder.DropTable(
                name: "broadcast_messages");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "request_events");

            migrationBuilder.DropTable(
                name: "request_key_items");

            migrationBuilder.DropTable(
                name: "resident_consents");

            migrationBuilder.DropTable(
                name: "resident_events");

            migrationBuilder.DropTable(
                name: "resident_memberships");

            migrationBuilder.DropTable(
                name: "resident_phones");

            migrationBuilder.DropColumn(
                name: "ProjectionVersion",
                table: "source_records");
        }
    }
}
