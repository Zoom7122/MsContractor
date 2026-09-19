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
        salesReturnRecreationItem.Property(item => item.RollbackPayloadJson).HasColumnType("jsonb");
        salesReturnRecreationItem.HasIndex(item => new { item.AccountId, item.OperationId });
        salesReturnRecreationItem.HasOne<SalesReturnRecreationOperation>()
            .WithMany(item => item.Items)
            .HasForeignKey(item => new { item.AccountId, item.OperationId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
