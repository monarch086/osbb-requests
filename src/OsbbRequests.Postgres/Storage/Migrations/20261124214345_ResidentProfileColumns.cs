using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OsbbRequests.Data.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ResidentProfileColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApartmentNumber",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "residents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildingNumber",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsentAt",
                table: "residents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsentVersion",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "residents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReason",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PersonalDataDeleted",
                table: "residents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersonalDataDeletedAt",
                table: "residents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationId",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResidentType",
                table: "residents",
                type: "text",
                nullable: false,
                defaultValue: "FamilyMember");

            migrationBuilder.AddColumn<string>(
                name: "ResumeAfterConsent",
                table: "residents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "residents",
                type: "text",
                nullable: false,
                defaultValue: "AwaitingPhone");

            migrationBuilder.AddColumn<bool>(
                name: "TypeBeforeAddress",
                table: "residents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TypeChangeCount",
                table: "residents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "residents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "residents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE residents
                SET
                    "RegistrationId" = "Payload"->>'Id',
                    "Stage" = COALESCE(CASE WHEN jsonb_typeof("Payload"->'Stage') = 'number'
                        THEN stages[("Payload"->>'Stage')::int + 1] END, "Payload"->>'Stage', 'AwaitingPhone'),
                    "Version" = COALESCE(("Payload"->>'Version')::integer, 0),
                    "CreatedAt" = ("Payload"->>'CreatedAt')::timestamptz,
                    "UpdatedAt" = ("Payload"->>'UpdatedAt')::timestamptz,
                    "PersonalDataDeletedAt" = ("Payload"->>'PersonalDataDeletedAt')::timestamptz,
                    "ApprovedAt" = ("Payload"->>'ApprovedAt')::timestamptz,
                    "ConsentAt" = ("Payload"->>'ConsentAt')::timestamptz,
                    "Phone" = "Payload"->>'Phone',
                    "FirstName" = "Payload"->>'FirstName',
                    "LastName" = "Payload"->>'LastName',
                    "DecisionReason" = "Payload"->>'DecisionReason',
                    "BuildingNumber" = "Payload"->>'BuildingNumber',
                    "ApartmentNumber" = "Payload"->>'ApartmentNumber',
                    "Email" = "Payload"->>'Email',
                    "ConsentVersion" = "Payload"->>'ConsentVersion',
                    "PersonalDataDeleted" = COALESCE(("Payload"->>'PersonalDataDeleted')::boolean, false),
                    "ResumeAfterConsent" = COALESCE(CASE WHEN jsonb_typeof("Payload"->'ResumeAfterConsent') = 'number'
                        THEN stages[("Payload"->>'ResumeAfterConsent')::int + 1] END, "Payload"->>'ResumeAfterConsent'),
                    "ResidentType" = COALESCE(CASE WHEN jsonb_typeof("Payload"->'ResidentType') = 'number'
                        THEN types[("Payload"->>'ResidentType')::int + 1] END, "Payload"->>'ResidentType', 'FamilyMember'),
                    "TypeBeforeAddress" = COALESCE(("Payload"->>'TypeBeforeAddress')::boolean, false),
                    "TypeChangeCount" = COALESCE(("Payload"->>'TypeChangeCount')::integer, 0)
                FROM (VALUES (
                    ARRAY['AwaitingPhone', 'AwaitingFirstName', 'AwaitingLastName', 'Pending', 'Approved',
                          'Rejected', 'Cancelled', 'AwaitingBuilding', 'AwaitingApartment', 'Deleted',
                          'AwaitingEmail', 'AwaitingConsent', 'AwaitingResidentType', 'AwaitingInitialResidentType'],
                    ARRAY['FamilyMember', 'Owner', 'Tenant', 'Employee']
                )) AS enums(stages, types);
                """);

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "residents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "residents",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.Sql("""
                UPDATE residents
                SET "Payload" = jsonb_strip_nulls(jsonb_build_object(
                    'UserId', "TelegramUserId",
                    'BuildingId', "ComplexId",
                    'Id', "RegistrationId",
                    'Stage', "Stage",
                    'Version', "Version",
                    'CreatedAt', "CreatedAt",
                    'UpdatedAt', "UpdatedAt",
                    'PersonalDataDeletedAt', "PersonalDataDeletedAt",
                    'ApprovedAt', "ApprovedAt",
                    'ConsentAt', "ConsentAt",
                    'Phone', "Phone",
                    'FirstName', "FirstName",
                    'LastName', "LastName",
                    'DecisionReason', "DecisionReason",
                    'BuildingNumber', "BuildingNumber",
                    'ApartmentNumber', "ApartmentNumber",
                    'Email', "Email",
                    'ConsentVersion', "ConsentVersion",
                    'PersonalDataDeleted', "PersonalDataDeleted",
                    'ResumeAfterConsent', "ResumeAfterConsent",
                    'ResidentType', "ResidentType",
                    'TypeBeforeAddress', "TypeBeforeAddress",
                    'TypeChangeCount', "TypeChangeCount"
                ));
                """);

            migrationBuilder.DropColumn(
                name: "ApartmentNumber",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "BuildingNumber",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "ConsentAt",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "ConsentVersion",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "DecisionReason",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "PersonalDataDeleted",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "PersonalDataDeletedAt",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "RegistrationId",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "ResidentType",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "ResumeAfterConsent",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "TypeBeforeAddress",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "TypeChangeCount",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "residents");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "residents");
        }
    }
}
