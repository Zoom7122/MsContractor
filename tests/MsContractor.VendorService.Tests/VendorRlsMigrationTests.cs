using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MsContractor.VendorService.Repo;

namespace MsContractor.VendorService.Tests;

public sealed class VendorRlsMigrationTests
{
    [Fact]
    public void RlsMigration_IsRegisteredAndProtectsInstallations()
    {
        var options = new DbContextOptionsBuilder<VendorDbContext>()
            .UseNpgsql("Host=localhost;Database=vendor;Username=postgres;Password=postgres")
            .Options;
        using var dbContext = new VendorDbContext(options);
        var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();

        var migrationType = migrationsAssembly.Migrations[
            "20260821000100_EnableInstallationRowLevelSecurity"];
        var migration = migrationsAssembly.CreateMigration(
            migrationType,
            dbContext.Database.ProviderName!);
        var sql = string.Join(
            Environment.NewLine,
            migration.UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql));

        Assert.Contains(
            "ALTER TABLE vendor.installations ENABLE ROW LEVEL SECURITY",
            sql);
        Assert.Contains(
            "ALTER TABLE vendor.installations FORCE ROW LEVEL SECURITY",
            sql);
        Assert.Contains(
            "CREATE POLICY account_isolation ON vendor.installations",
            sql);
        Assert.Contains("current_setting('app.account_id', true)", sql);
        Assert.Contains("WITH CHECK", sql);
    }
}
