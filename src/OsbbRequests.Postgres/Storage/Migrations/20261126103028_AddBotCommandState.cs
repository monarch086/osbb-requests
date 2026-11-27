using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Postgres.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddBotCommandState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bot_command_receipts",
                columns: table => new
                {
                    ComplexId = table.Column<long>(type: "bigint", nullable: false),
                    Bot = table.Column<string>(type: "text", nullable: false),
                    UpdateId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Keyboard = table.Column<int>(type: "integer", nullable: true),
                    Delivered = table.Column<bool>(type: "boolean", nullable: false),
                    RefreshMenu = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_command_receipts", x => new { x.ComplexId, x.Bot, x.UpdateId });
                    table.ForeignKey(
                        name: "FK_bot_command_receipts_complexes_ComplexId",
                        column: x => x.ComplexId,
                        principalTable: "complexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bot_conversations",
                columns: table => new
                {
                    ComplexId = table.Column<long>(type: "bigint", nullable: false),
                    Bot = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_conversations", x => new { x.ComplexId, x.Bot, x.UserId, x.Kind });
                    table.ForeignKey(
                        name: "FK_bot_conversations_complexes_ComplexId",
                        column: x => x.ComplexId,
                        principalTable: "complexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_command_receipts");

            migrationBuilder.DropTable(
                name: "bot_conversations");
        }
    }
}
