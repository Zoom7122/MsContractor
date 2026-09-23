using MsContractor.CatalogSyncService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;
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
            "20260906125521_PersistSalesReturnRecreationRequest",
            "20260914120000_RemoveSalesReturnRecreationRequest",
            "20260916120000_RemoveDocumentAdditionalData"
        ];
        Assert.Equal(expected, context.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

    [Fact]
    public void RemoveDocumentAdditionalDataMigration_DropsAndFullyRestoresTable()
    {
        using var context = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres").Options);
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var migrationType = migrationsAssembly.Migrations["20260916120000_RemoveDocumentAdditionalData"];
        var migration = migrationsAssembly.CreateMigration(migrationType, context.Database.ProviderName!);

        var drop = Assert.Single(migration.UpOperations.OfType<DropTableOperation>());
        Assert.Equal("catalog_sync", drop.Schema);
        Assert.Equal("document_additional_data", drop.Name);

        var create = Assert.Single(migration.DownOperations.OfType<CreateTableOperation>());
        Assert.Equal("catalog_sync", create.Schema);
        Assert.Equal("document_additional_data", create.Name);
        Assert.Equal(["DocumentId", "RawJson"], create.Columns.Select(column => column.Name));
        var foreignKey = Assert.Single(create.ForeignKeys);
        Assert.Equal("counterparty_documents", foreignKey.PrincipalTable);
        Assert.Equal(ReferentialAction.Cascade, foreignKey.OnDelete);

        var sql = string.Join(Environment.NewLine,
            migration.DownOperations.OfType<SqlOperation>().Select(operation => operation.Sql));
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql);
        Assert.Contains("FORCE ROW LEVEL SECURITY", sql);
        Assert.Contains("CREATE POLICY account_isolation", sql);
    }

    [Fact]
    public void EgressMigrations_IncludeSalesReturnJournalRemoval()
    {
        using var context = new EgressDbContext(new DbContextOptionsBuilder<EgressDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres").Options);

        Assert.Equal(
            [
                "20260906110125_AddSalesReturnOperationJournal",
                "20260914120000_RemoveSalesReturnOperationJournal",
                "20260916130000_AddSalesReturnRawData",
                "20260916140000_AddAccountIdToSalesReturnRawData",
                "20260916150000_AddSalesReturnPositionsRawData",
                "20260916160000_AddSalesReturnRecreationOperations",
                "20260920120000_LinkSalesReturnRecreationItemsToRawData",
                "20260920130000_RemoveSalesReturnRollbackData",
                "20260920140000_AddPurchaseReturnPreparationSnapshots",
                "20260921100000_AddSalesReturnRelationSnapshots",
                "20260922100000_AddPurchaseReturnRelatedRawData",
                "20260922110000_AddPurchaseReturnMoneyRelationSnapshots",
                "20260923120000_AddFactureInRawData",
                "20260923130000_AddFactureInRecreationItems"
            ],
            context.GetService<IMigrationsAssembly>().Migrations.Keys);
    }

    [Fact]
    public void RollbackDataMigrationDropsRollbackColumns()
    {
        using var context = new EgressDbContext(new DbContextOptionsBuilder<EgressDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres").Options);
        var migration = context.GetService<IMigrationsAssembly>().CreateMigration(
            context.GetService<IMigrationsAssembly>().Migrations[
                "20260920130000_RemoveSalesReturnRollbackData"],
            context.Database.ProviderName!);

        var droppedColumns = migration.UpOperations
            .OfType<DropColumnOperation>()
            .Select(operation => operation.Name)
            .ToArray();
        Assert.Equal(["RollbackDocumentId", "RollbackPayloadJson", "RollbackSyncId"], droppedColumns);
    }

    [Fact]
    public void RecreationItemsMigrationBackfillsRawDataAndAddsRestrictiveForeignKey()
    {
        using var context = new EgressDbContext(new DbContextOptionsBuilder<EgressDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres").Options);
        var migration = context.GetService<IMigrationsAssembly>().CreateMigration(
            context.GetService<IMigrationsAssembly>().Migrations[
                "20260920120000_LinkSalesReturnRecreationItemsToRawData"],
            context.Database.ProviderName!);

        var sql = string.Join(Environment.NewLine,
            migration.UpOperations.OfType<SqlOperation>().Select(operation => operation.Sql));
        Assert.Contains("INSERT INTO egress.salesreturn_raw_data", sql);
        var foreignKey = Assert.Single(migration.UpOperations.OfType<AddForeignKeyOperation>());
        Assert.Equal("salesreturn_recreation_items", foreignKey.Table);
        Assert.Equal(["AccountId", "SourceDocumentId"], foreignKey.Columns!);
        Assert.Equal("salesreturn_raw_data", foreignKey.PrincipalTable);
        Assert.Equal(["AccountId", "DocumentId"], foreignKey.PrincipalColumns!);
        Assert.Equal(ReferentialAction.Restrict, foreignKey.OnDelete);
    }

    [Fact]
    public void EgressSnapshot_MatchesCurrentModel()
    {
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseNpgsql("Host=localhost;Database=egress;Username=postgres;Password=postgres")
            .Options;
        using var context = new EgressDbContext(options);
        var snapshotType = typeof(EgressDbContext).Assembly.GetType(
            "MsContractor.MoySkladEgressService.Persistence.Migrations.EgressDbContextModelSnapshot")!;
        var snapshot = (ModelSnapshot)Activator.CreateInstance(snapshotType, nonPublic: true)!;
        var runtimeInitializer = context.GetService<IModelRuntimeInitializer>();
        var snapshotModel = runtimeInitializer.Initialize(snapshot.Model, designTime: true);
        var differences = context.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(
            differences.Count == 0,
            string.Join(Environment.NewLine, differences.Select(Describe)));
    }

}
