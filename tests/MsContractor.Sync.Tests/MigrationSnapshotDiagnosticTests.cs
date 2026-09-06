using MsContractor.CatalogSyncService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;

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
            "MsContractor.CatalogSyncService.Persistence.Migrations.CatalogSyncDbContextModelSnapshot")!;
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
    [Fact]
    public void MigrationIds_ArePreserved()
    {
        using var context = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres").Options);
        string[] expected =
        [
            "20260730145227_InitialCatalogSync",
            "20260805000100_AddIncrementalCounterpartySync",
            "20260805000200_LinkCounterpartiesToSyncRunsAndRemoveMoySkladId",
            "20260807000100_AddMergeJobs",
            "20260821000100_EnableTenantRowLevelSecurity",
            "20260821000200_AddCounterpartyDocuments",
            "20260901000100_AddCounterpartyDocumentCounterpartyForeignKey",
            "20260902000100_AddCounterpartyDocumentAdditionalData",
            "20260904000100_AddSalesReturnAdditionalData",
            "20260904000200_RenameDocumentAdditionalTables",
            "20260906125521_PersistSalesReturnRecreationRequest"
        ];
        Assert.Equal(expected, context.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

}
