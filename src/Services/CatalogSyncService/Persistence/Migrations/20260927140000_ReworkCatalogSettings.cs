using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260927140000_ReworkCatalogSettings")]
public sealed class ReworkCatalogSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The existing JSON settings are intentionally reset as requested.
        migrationBuilder.DropTable(name: "catalog_settings", schema: "catalog_sync");

        migrationBuilder.CreateTable(
            name: "catalog_settings",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                IncludeArchivedWithDocuments = table.Column<bool>(type: "boolean", nullable: false),
                GroupLimit = table.Column<int>(type: "integer", nullable: false),
                ItemLimit = table.Column<int>(type: "integer", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_catalog_settings", item => item.AccountId));

        migrationBuilder.CreateTable(
            name: "catalog_setting_exclusions",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Field = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Value = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_catalog_setting_exclusions",
                    item => new { item.AccountId, item.Field, item.Value });
                table.ForeignKey(
                    name: "FK_catalog_setting_exclusions_catalog_settings_AccountId",
                    columns: item => item.AccountId,
                    principalSchema: "catalog_sync",
                    principalTable: "catalog_settings",
                    principalColumns: new[] { "AccountId" },
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint(
                    "CK_catalog_setting_exclusions_Field",
                    "\"Field\" IN ('name', 'email', 'phone')");
            });

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.catalog_settings ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.catalog_settings FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.catalog_settings
                USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);

            ALTER TABLE catalog_sync.catalog_setting_exclusions ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.catalog_setting_exclusions FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.catalog_setting_exclusions
                USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.catalog_settings DISABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.catalog_setting_exclusions DISABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.catalog_settings ADD COLUMN "Payload" jsonb;

            UPDATE catalog_sync.catalog_settings AS settings
            SET "Payload" = jsonb_build_object(
                'duplicateExclusions', COALESCE(
                    (
                        SELECT jsonb_agg(
                            jsonb_build_object('field', exclusion."Field", 'value', exclusion."Value")
                            ORDER BY exclusion."Field", exclusion."Value")
                        FROM catalog_sync.catalog_setting_exclusions AS exclusion
                        WHERE exclusion."AccountId" = settings."AccountId"
                    ),
                    '[]'::jsonb),
                'duplicateSearchOptions', jsonb_build_object(
                    'includeArchivedWithDocuments', settings."IncludeArchivedWithDocuments"),
                'searchLimits', jsonb_build_object(
                    'groupLimit', settings."GroupLimit",
                    'itemLimit', settings."ItemLimit"));

            ALTER TABLE catalog_sync.catalog_settings ALTER COLUMN "Payload" SET NOT NULL;
            """);

        migrationBuilder.DropTable(name: "catalog_setting_exclusions", schema: "catalog_sync");
        migrationBuilder.DropColumn(
            name: "IncludeArchivedWithDocuments",
            schema: "catalog_sync",
            table: "catalog_settings");
        migrationBuilder.DropColumn(
            name: "GroupLimit",
            schema: "catalog_sync",
            table: "catalog_settings");
        migrationBuilder.DropColumn(
            name: "ItemLimit",
            schema: "catalog_sync",
            table: "catalog_settings");

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.catalog_settings ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.catalog_settings FORCE ROW LEVEL SECURITY;
            """);
    }
}
