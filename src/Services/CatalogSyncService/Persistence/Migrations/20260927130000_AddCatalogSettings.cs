using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260927130000_AddCatalogSettings")]
public sealed class AddCatalogSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "catalog_settings",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_catalog_settings", x => x.AccountId));

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.catalog_settings ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.catalog_settings FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.catalog_settings
                USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "catalog_settings", schema: "catalog_sync");
    }
}
