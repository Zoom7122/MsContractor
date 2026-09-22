using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;

namespace MsContractor.MoySkladEgressService.Persistence;

public sealed class EgressDbContext : DbContext
{
    public EgressDbContext(DbContextOptions<EgressDbContext> options)
        : base(options)
    {
    }

    public DbSet<SalesReturnRawData> SalesReturnRawData => Set<SalesReturnRawData>();

    public DbSet<SalesReturnPositionRawData> SalesReturnPositionRawData => Set<SalesReturnPositionRawData>();

    public DbSet<SalesReturnRecreationOperation> SalesReturnRecreationOperations => Set<SalesReturnRecreationOperation>();

    public DbSet<SalesReturnRecreationItem> SalesReturnRecreationItems => Set<SalesReturnRecreationItem>();

    public DbSet<SalesReturnRelationsSnapshotRecord> SalesReturnRelationsSnapshots => Set<SalesReturnRelationsSnapshotRecord>();

    public DbSet<PaymentOutRelationSnapshotRecord> PaymentOutRelationSnapshots => Set<PaymentOutRelationSnapshotRecord>();

    public DbSet<CashOutRelationSnapshotRecord> CashOutRelationSnapshots => Set<CashOutRelationSnapshotRecord>();

    public DbSet<LossRelationSnapshotRecord> LossRelationSnapshots => Set<LossRelationSnapshotRecord>();

    public DbSet<PurchaseReturnRawData> PurchaseReturnRawData => Set<PurchaseReturnRawData>();

    public DbSet<PurchaseReturnPositionRawData> PurchaseReturnPositionRawData => Set<PurchaseReturnPositionRawData>();

    public DbSet<PurchaseReturnFactureOutRawData> PurchaseReturnFactureOutRawData => Set<PurchaseReturnFactureOutRawData>();

    public DbSet<PurchaseReturnFactureInRawData> PurchaseReturnFactureInRawData => Set<PurchaseReturnFactureInRawData>();

    public DbSet<PurchaseReturnPaymentInRawData> PurchaseReturnPaymentInRawData => Set<PurchaseReturnPaymentInRawData>();

    public DbSet<PurchaseReturnCashInRawData> PurchaseReturnCashInRawData => Set<PurchaseReturnCashInRawData>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("egress");

        var salesReturnRawData = modelBuilder.Entity<SalesReturnRawData>();
        salesReturnRawData.ToTable("salesreturn_raw_data");
        salesReturnRawData.HasKey(item => new { item.AccountId, item.DocumentId });
        salesReturnRawData.Property(item => item.RawJson).HasColumnType("jsonb");

        var salesReturnPositionRawData = modelBuilder.Entity<SalesReturnPositionRawData>();
        salesReturnPositionRawData.ToTable("salesreturn_positions_raw_data");
        salesReturnPositionRawData.HasKey(item => new
        {
            item.AccountId,
            item.SalesReturnId,
            item.PositionId
        });
        salesReturnPositionRawData.Property(item => item.RawJson).HasColumnType("jsonb");
        salesReturnPositionRawData.HasIndex(item => new { item.AccountId, item.SalesReturnId });
        salesReturnPositionRawData.HasOne<SalesReturnRawData>()
            .WithMany()
            .HasForeignKey(item => new { item.AccountId, item.SalesReturnId })
            .HasPrincipalKey(item => new { item.AccountId, item.DocumentId })
            .OnDelete(DeleteBehavior.Cascade);

        var salesReturnRecreationOperation = modelBuilder.Entity<SalesReturnRecreationOperation>();
        salesReturnRecreationOperation.ToTable("salesreturn_recreation_operations");
        salesReturnRecreationOperation.HasKey(item => new { item.AccountId, item.OperationId });

        var salesReturnRecreationItem = modelBuilder.Entity<SalesReturnRecreationItem>();
        salesReturnRecreationItem.ToTable("salesreturn_recreation_items");
        salesReturnRecreationItem.HasKey(item => new { item.AccountId, item.OperationId, item.SourceDocumentId });
        salesReturnRecreationItem.Property(item => item.SourceRawJson).HasColumnType("jsonb");
        salesReturnRecreationItem.Property(item => item.NewPayloadJson).HasColumnType("jsonb");
        salesReturnRecreationItem.HasIndex(item => new { item.AccountId, item.OperationId });
        salesReturnRecreationItem.HasOne<SalesReturnRecreationOperation>()
            .WithMany(item => item.Items)
            .HasForeignKey(item => new { item.AccountId, item.OperationId })
            .OnDelete(DeleteBehavior.Cascade);
        salesReturnRecreationItem.HasOne<SalesReturnRawData>()
            .WithMany()
            .HasForeignKey(item => new { item.AccountId, item.SourceDocumentId })
            .HasPrincipalKey(item => new { item.AccountId, item.DocumentId })
            .OnDelete(DeleteBehavior.Restrict);

        var relationsSnapshot = modelBuilder.Entity<SalesReturnRelationsSnapshotRecord>();
        relationsSnapshot.ToTable("salesreturn_relation_snapshots");
        relationsSnapshot.HasKey(item => new
        {
            item.AccountId,
            item.OperationId,
            item.SourceSalesReturnId
        });
        relationsSnapshot.HasIndex(item => new { item.AccountId, item.OperationId });
        relationsSnapshot.HasOne<SalesReturnRecreationItem>()
            .WithMany()
            .HasForeignKey(item => new
            {
                item.AccountId,
                item.OperationId,
                item.SourceSalesReturnId
            })
            .HasPrincipalKey(item => new
            {
                item.AccountId,
                item.OperationId,
                item.SourceDocumentId
            })
            .OnDelete(DeleteBehavior.Cascade);

        var paymentOutSnapshot = modelBuilder.Entity<PaymentOutRelationSnapshotRecord>();
        paymentOutSnapshot.ToTable("salesreturn_paymentout_relations");
        paymentOutSnapshot.HasKey(item => new
        {
            item.AccountId,
            item.OperationId,
            item.SourceSalesReturnId,
            item.DocumentId
        });
        paymentOutSnapshot.Property(item => item.OperationsBeforeJson).HasColumnType("jsonb");
        paymentOutSnapshot.Property(item => item.LinkedSum).HasColumnType("numeric");
        paymentOutSnapshot.HasOne<SalesReturnRelationsSnapshotRecord>()
            .WithMany(item => item.PaymentOuts)
            .HasForeignKey(item => new
            {
                item.AccountId,
                item.OperationId,
                item.SourceSalesReturnId
            })
            .OnDelete(DeleteBehavior.Cascade);

        var cashOutSnapshot = modelBuilder.Entity<CashOutRelationSnapshotRecord>();
        cashOutSnapshot.ToTable("salesreturn_cashout_relations");
        cashOutSnapshot.HasKey(item => new
        {
            item.AccountId,
            item.OperationId,
            item.SourceSalesReturnId,
            item.DocumentId
        });
        cashOutSnapshot.Property(item => item.OperationsBeforeJson).HasColumnType("jsonb");
        cashOutSnapshot.Property(item => item.LinkedSum).HasColumnType("numeric");
        cashOutSnapshot.HasOne<SalesReturnRelationsSnapshotRecord>()
            .WithMany(item => item.CashOuts)
            .HasForeignKey(item => new
            {
                item.AccountId,
                item.OperationId,
                item.SourceSalesReturnId
            })
            .OnDelete(DeleteBehavior.Cascade);

        var lossSnapshot = modelBuilder.Entity<LossRelationSnapshotRecord>();
        lossSnapshot.ToTable("salesreturn_loss_relations");
        lossSnapshot.HasKey(item => new
        {
            item.AccountId,
            item.OperationId,
            item.SourceSalesReturnId,
            item.DocumentId
        });
        lossSnapshot.Property(item => item.SalesReturnBeforeJson).HasColumnType("jsonb");
        lossSnapshot.HasOne<SalesReturnRelationsSnapshotRecord>()
            .WithMany(item => item.Losses)
            .HasForeignKey(item => new
            {
                item.AccountId,
                item.OperationId,
                item.SourceSalesReturnId
            })
            .OnDelete(DeleteBehavior.Cascade);

        var purchaseReturnRawData = modelBuilder.Entity<PurchaseReturnRawData>();
        purchaseReturnRawData.ToTable("purchasereturn_raw_data");
        purchaseReturnRawData.HasKey(item => new { item.AccountId, item.DocumentId });
        purchaseReturnRawData.Property(item => item.RawJson).HasColumnType("jsonb");

        var purchaseReturnPositionRawData = modelBuilder.Entity<PurchaseReturnPositionRawData>();
        purchaseReturnPositionRawData.ToTable("purchasereturn_positions_raw_data");
        purchaseReturnPositionRawData.HasKey(item => new
        {
            item.AccountId,
            item.PurchaseReturnId,
            item.PositionId
        });
        purchaseReturnPositionRawData.Property(item => item.RawJson).HasColumnType("jsonb");
        purchaseReturnPositionRawData.HasIndex(item => new { item.AccountId, item.PurchaseReturnId });
        purchaseReturnPositionRawData.HasOne<PurchaseReturnRawData>()
            .WithMany()
            .HasForeignKey(item => new { item.AccountId, item.PurchaseReturnId })
            .HasPrincipalKey(item => new { item.AccountId, item.DocumentId })
            .OnDelete(DeleteBehavior.Cascade);

        ConfigurePurchaseReturnRelatedRawData<PurchaseReturnFactureOutRawData>(
            modelBuilder,
            "purchasereturn_factureout_raw_data");
        ConfigurePurchaseReturnRelatedRawData<PurchaseReturnFactureInRawData>(
            modelBuilder,
            "purchasereturn_facturein_raw_data");
        ConfigurePurchaseReturnRelatedRawData<PurchaseReturnPaymentInRawData>(
            modelBuilder,
            "purchasereturn_paymentin_raw_data");
        ConfigurePurchaseReturnRelatedRawData<PurchaseReturnCashInRawData>(
            modelBuilder,
            "purchasereturn_cashin_raw_data");
    }

    private static void ConfigurePurchaseReturnRelatedRawData<TEntity>(
        ModelBuilder modelBuilder,
        string tableName)
        where TEntity : class
    {
        var entity = modelBuilder.Entity<TEntity>();
        entity.ToTable(tableName);
        entity.HasKey("AccountId", "PurchaseReturnId", "DocumentId");
        entity.Property<string>("RawJson").HasColumnType("jsonb");
        entity.HasIndex("AccountId", "PurchaseReturnId");
        entity.HasOne<PurchaseReturnRawData>()
            .WithMany()
            .HasForeignKey("AccountId", "PurchaseReturnId")
            .HasPrincipalKey(item => new { item.AccountId, item.DocumentId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
