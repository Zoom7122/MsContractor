using MsContractor.VendorService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

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
    [Fact]
    public void Snapshot_MatchesCurrentRelationalModel()
    {
        using var context = new VendorDbContext(new DbContextOptionsBuilder<VendorDbContext>()
            .UseNpgsql("Host=localhost;Database=vendor;Username=postgres;Password=postgres").Options);
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!;
        var initialized = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot.Model, designTime: true);
        var current = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        Assert.Empty(context.GetService<IMigrationsModelDiffer>().GetDifferences(initialized.GetRelationalModel(), current.GetRelationalModel()));
    }

    [Fact]
    public void MigrationIds_ArePreserved()
    {
        using var context = new VendorDbContext(new DbContextOptionsBuilder<VendorDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres").Options);
        string[] expected =
        [
            "20260725192108_CreateVendorSchema",
            "20260726100000_AddOutboxRequestId",
            "20260821000100_EnableInstallationRowLevelSecurity"
        ];
        Assert.Equal(expected, context.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

}
