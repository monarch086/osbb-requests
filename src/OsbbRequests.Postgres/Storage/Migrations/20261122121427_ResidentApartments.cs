using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.DataImport.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ResidentApartments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resident_apartments",
                columns: table => new
                {
                    Source = table.Column<string>(type: "text", nullable: false),
                    ComplexId = table.Column<string>(type: "text", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: false),
                    ApartmentId = table.Column<string>(type: "text", nullable: false),
                    RegistrationId = table.Column<string>(type: "text", nullable: false),
                    BuildingNumber = table.Column<string>(type: "text", nullable: false),
                    ApartmentNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resident_apartments", x => new { x.Source, x.ComplexId, x.TelegramUserId, x.ApartmentId });
                    table.CheckConstraint("CK_resident_apartments_number", "\"ApartmentNumber\" BETWEEN 1 AND 999");
                    table.CheckConstraint("CK_resident_apartments_status", "\"Status\" IN ('Pending', 'Approved', 'Rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_resident_apartments_Source_ComplexId_BuildingNumber_Apartme~",
                table: "resident_apartments",
                columns: new[] { "Source", "ComplexId", "BuildingNumber", "ApartmentNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resident_apartments");
        }
    }
}
