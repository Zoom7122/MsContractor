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

    public DbSet<PurchaseReturnRawData> PurchaseReturnRawData => Set<PurchaseReturnRawData>();

    public DbSet<PurchaseReturnPositionRawData> PurchaseReturnPositionRawData => Set<PurchaseReturnPositionRawData>();

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
    }
}
