using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.VendorService.Persistence.Migrations;

[DbContext(typeof(VendorDbContext))]
[Migration("20260821000100_EnableInstallationRowLevelSecurity")]
public sealed class EnableInstallationRowLevelSecurity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE vendor.installations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE vendor.installations FORCE ROW LEVEL SECURITY;

            CREATE POLICY account_isolation ON vendor.installations
                USING (
                    account_id = NULLIF(current_setting('app.account_id', true), '')::uuid
                )
                WITH CHECK (
                    account_id = NULLIF(current_setting('app.account_id', true), '')::uuid
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS account_isolation ON vendor.installations;
            ALTER TABLE vendor.installations DISABLE ROW LEVEL SECURITY;
            """);
    }
}
