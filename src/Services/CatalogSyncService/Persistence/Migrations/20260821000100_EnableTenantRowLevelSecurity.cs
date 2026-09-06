using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260821000100_EnableTenantRowLevelSecurity")]
public sealed class EnableTenantRowLevelSecurity : Migration
{
    private static readonly string[] TenantTables =
    [
        "sync_runs",
        "sync_watermarks",
        "counterparties",
        "merge_jobs",
        "merge_operations"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            migrationBuilder.Sql($$"""
                ALTER TABLE catalog_sync.{{table}} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.{{table}} FORCE ROW LEVEL SECURITY;

                CREATE POLICY account_isolation ON catalog_sync.{{table}}
                    USING (
                        "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                    )
                    WITH CHECK (
                        "AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                    );
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            migrationBuilder.Sql($$"""
                DROP POLICY IF EXISTS account_isolation ON catalog_sync.{{table}};
                ALTER TABLE catalog_sync.{{table}} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
