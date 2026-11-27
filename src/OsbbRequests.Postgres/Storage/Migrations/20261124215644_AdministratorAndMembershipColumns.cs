using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.DataImport.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AdministratorAndMembershipColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RegistrationId",
                table: "resident_memberships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "resident_memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "resident_memberships",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "administrators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "administrators",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "administrators",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "administrators",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "administrators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "administrators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE administrators
                SET "Email" = "Data"->>'Email',
                    "DisplayName" = "Data"->>'DisplayName',
                    "IsActive" = COALESCE(("Data"->>'IsActive')::boolean, false),
                    "Version" = COALESCE(("Data"->>'Version')::integer, 0),
                    "CreatedAt" = ("Data"->>'CreatedAt')::timestamptz,
                    "UpdatedAt" = ("Data"->>'UpdatedAt')::timestamptz;

                UPDATE resident_memberships
                SET "ComplexId" = COALESCE(NULLIF("ComplexId", ''), "Data"->>'buildingId', ''),
                    "RegistrationId" = "Data"->>'registrationId',
                    "ReviewedBy" = "Data"->>'reviewedBy',
                    "ReviewedAt" = ("Data"->>'reviewedAt')::timestamptz;
                """);

            migrationBuilder.DropColumn(
                name: "Data",
                table: "resident_memberships");

            migrationBuilder.DropColumn(
                name: "Data",
                table: "administrators");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "resident_memberships",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "administrators",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.Sql("""
                UPDATE administrators AS a
                SET "Data" = COALESCE(
                    (SELECT s."Data" FROM source_records AS s
                     WHERE s."Source" = a."Source" AND s."Table" = 'administrators' AND s."Key" = a."UserId"),
                    jsonb_strip_nulls(jsonb_build_object(
                        'UserId', a."UserId", 'Email', a."Email", 'DisplayName', a."DisplayName",
                        'IsActive', a."IsActive", 'Version', a."Version",
                        'CreatedAt', a."CreatedAt", 'UpdatedAt', a."UpdatedAt"
                    ))
                );

                UPDATE resident_memberships AS m
                SET "Data" = COALESCE(
                    (SELECT s."Data"->'recordData' FROM source_records AS s
                     WHERE s."Source" = m."Source" AND s."Table" = 'residents' AND s."Key" = m."Key"
                       AND jsonb_typeof(s."Data"->'recordData') = 'object'),
                    jsonb_strip_nulls(jsonb_build_object(
                        'pk', 'USER#' || m."TelegramUserId", 'sk', 'MEMBERSHIP',
                        'buildingId', m."ComplexId", 'active', m."Active", 'registrationId', m."RegistrationId",
                        'reviewedBy', m."ReviewedBy", 'reviewedAt', m."ReviewedAt"
                    ))
                );
                """);

            migrationBuilder.DropColumn(
                name: "RegistrationId",
                table: "resident_memberships");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "resident_memberships");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "resident_memberships");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "administrators");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "administrators");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "administrators");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "administrators");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "administrators");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "administrators");
        }
    }
}
