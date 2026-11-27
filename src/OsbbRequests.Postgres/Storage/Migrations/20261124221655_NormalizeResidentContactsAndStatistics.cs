using Microsoft.EntityFrameworkCore.Migrations;

namespace OsbbRequests.DataImport.Storage.Migrations;

public partial class NormalizeResidentContactsAndStatistics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");

        migrationBuilder.AddColumn<bool>(
            name: "IsPrimary", table: "resident_phones", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.Sql("""
            ALTER TABLE resident_consents ALTER COLUMN "ConsentedAt" DROP NOT NULL;

            DELETE FROM resident_phones AS phone
            USING residents AS resident
            WHERE phone."ResidentId" = resident."Id"
              AND (resident."PersonalDataDeleted" OR resident."Stage" = 'Deleted');

            DELETE FROM resident_consents AS consent
            USING residents AS resident
            WHERE consent."ResidentId" = resident."Id"
              AND (resident."PersonalDataDeleted" OR resident."Stage" = 'Deleted');

            INSERT INTO resident_phones ("ResidentId", "Phone", "IsPrimary")
            SELECT "Id", "Phone", true
            FROM residents
            WHERE NULLIF("Phone", '') IS NOT NULL AND NOT "PersonalDataDeleted" AND "Stage" <> 'Deleted'
            ON CONFLICT ("ResidentId", "Phone") DO UPDATE SET "IsPrimary" = true;

            INSERT INTO resident_consents ("ResidentId", "ConsentedAt", "Version")
            SELECT "Id", "ConsentAt", "ConsentVersion"
            FROM residents
            WHERE ("ConsentAt" IS NOT NULL OR "ConsentVersion" IS NOT NULL)
              AND NOT "PersonalDataDeleted" AND "Stage" <> 'Deleted'
            ON CONFLICT ("ResidentId") DO NOTHING;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_resident_phones_ResidentId", table: "resident_phones",
            column: "ResidentId", unique: true, filter: "\"IsPrimary\"");

        migrationBuilder.DropColumn("Phone", "residents");
        migrationBuilder.DropColumn("ConsentAt", "residents");
        migrationBuilder.DropColumn("ConsentVersion", "residents");

        migrationBuilder.DropPrimaryKey("PK_api_daily_statistics", "api_daily_statistics");
        migrationBuilder.Sql("""
            ALTER TABLE api_daily_statistics ALTER COLUMN "Date" TYPE date USING "Date"::date;
            """);
        migrationBuilder.AddPrimaryKey("PK_api_daily_statistics", "api_daily_statistics", ["SourceId", "Date", "Endpoint"]);
        migrationBuilder.DropColumn("Key", "api_daily_statistics");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(349875242);");

        migrationBuilder.AddColumn<string>("Phone", "residents", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("ConsentAt", "residents", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("ConsentVersion", "residents", type: "text", nullable: true);
        migrationBuilder.Sql("""
            UPDATE residents AS resident SET "Phone" = phone."Phone"
            FROM resident_phones AS phone
            WHERE phone."ResidentId" = resident."Id" AND phone."IsPrimary";

            UPDATE residents AS resident SET "ConsentAt" = consent."ConsentedAt", "ConsentVersion" = consent."Version"
            FROM resident_consents AS consent
            WHERE consent."ResidentId" = resident."Id";

            DELETE FROM resident_consents WHERE "ConsentedAt" IS NULL;
            ALTER TABLE resident_consents ALTER COLUMN "ConsentedAt" SET NOT NULL;
            """);
        migrationBuilder.DropIndex("IX_resident_phones_ResidentId", "resident_phones");
        migrationBuilder.DropColumn("IsPrimary", "resident_phones");

        migrationBuilder.DropPrimaryKey("PK_api_daily_statistics", "api_daily_statistics");
        migrationBuilder.Sql("""
            ALTER TABLE api_daily_statistics ALTER COLUMN "Date" TYPE text USING to_char("Date", 'YYYY-MM-DD');
            ALTER TABLE api_daily_statistics ADD COLUMN "Key" text;
            UPDATE api_daily_statistics SET "Key" = 'API' || chr(31) || "Date" || '#' || "Endpoint";
            ALTER TABLE api_daily_statistics ALTER COLUMN "Key" SET NOT NULL;
            """);
        migrationBuilder.AddPrimaryKey("PK_api_daily_statistics", "api_daily_statistics", ["SourceId", "Key"]);
    }
}
