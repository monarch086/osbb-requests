using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Data.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialDataSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "administrators",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrators", x => new { x.Source, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ReadCount = table.Column<long>(type: "bigint", nullable: false),
                    ChangedCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requests", x => new { x.Source, x.OwnerId, x.RequestId });
                });

            migrationBuilder.CreateTable(
                name: "residents",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_residents", x => new { x.Source, x.ComplexId, x.TelegramUserId });
                });

            migrationBuilder.CreateTable(
                name: "source_records",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    Table = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_records", x => new { x.Source, x.Table, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_requests_Source_ComplexId_Status_CreatedAt",
                table: "requests",
                columns: new[] { "Source", "ComplexId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "administrators");

            migrationBuilder.DropTable(
                name: "import_runs");

            migrationBuilder.DropTable(
                name: "requests");

            migrationBuilder.DropTable(
                name: "residents");

            migrationBuilder.DropTable(
                name: "source_records");
        }
    }
}
