using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using MsContractor.CatalogSyncService.Repo;

namespace MsContractor.Sync.Tests;

public sealed class MigrationSnapshotDiagnosticTests
{
    [Fact]
    public void RlsMigration_IsRegisteredAndCoversEveryTenantTable()
    {
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog_sync;Username=postgres;Password=postgres")
            .Options;
        using var dbContext = new CatalogSyncDbContext(options);
        var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();

        var migrationType = migrationsAssembly.Migrations[
            "20260821000100_EnableTenantRowLevelSecurity"];
        var migration = migrationsAssembly.CreateMigration(
            migrationType,
            dbContext.Database.ProviderName!);
        var sql = string.Join(
            Environment.NewLine,
            migration.UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql));

        foreach (var table in new[]
                 {
                     "sync_runs", "sync_watermarks", "counterparties",
                     "merge_jobs", "merge_operations"
                 })
        {
            Assert.Contains($"ALTER TABLE catalog_sync.{table} ENABLE ROW LEVEL SECURITY", sql);
            Assert.Contains($"CREATE POLICY account_isolation ON catalog_sync.{table}", sql);
        }

        Assert.Contains("current_setting('app.account_id', true)", sql);
        Assert.Contains("WITH CHECK", sql);
    }

    [Fact]
    public void Snapshot_MatchesCurrentModel()
    {
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog_sync;Username=postgres;Password=postgres")
            .Options;
        using var dbContext = new CatalogSyncDbContext(options);
        var snapshotType = typeof(CatalogSyncDbContext).Assembly.GetType(
            "MsContractor.CatalogSyncService.Repo.Migrations.CatalogSyncDbContextModelSnapshot")!;
        var snapshot = (ModelSnapshot)Activator.CreateInstance(snapshotType, nonPublic: true)!;
        var differ = dbContext.GetService<IMigrationsModelDiffer>();
        var modelRuntimeInitializer = dbContext.GetService<IModelRuntimeInitializer>();
        var snapshotModel = modelRuntimeInitializer.Initialize(snapshot.Model, designTime: true);
        var currentModel = dbContext.GetService<IDesignTimeModel>().Model;
        var differences = differ.GetDifferences(
            snapshotModel.GetRelationalModel(),
            currentModel.GetRelationalModel());

        Assert.True(
            differences.Count == 0,
            string.Join(Environment.NewLine, differences.Select(Describe)));
    }

    private static string Describe(MigrationOperation operation) => operation switch
    {
        CreateIndexOperation index =>
            $"CreateIndex: {index.Schema}.{index.Table} ({string.Join(", ", index.Columns)}) unique={index.IsUnique}",
        _ => $"{operation.GetType().Name}: {operation}"
    };
}
