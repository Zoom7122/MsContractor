using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;

namespace MsContractor.MoySkladEgressService.Persistence;

public sealed class EgressDbContext(DbContextOptions<EgressDbContext> options) : DbContext(options)
{
    public DbSet<SalesReturnOperation> SalesReturnOperations => Set<SalesReturnOperation>();
    public DbSet<SalesReturnClaim> SalesReturnClaims => Set<SalesReturnClaim>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("egress");
        var operation = modelBuilder.Entity<SalesReturnOperation>();
        operation.ToTable("salesreturn_operations");
        operation.HasKey(x => new { x.AccountId, x.OperationId });
        operation.Property(x => x.RequestJson).HasColumnType("jsonb");
        operation.Property(x => x.ItemsJson).HasColumnType("jsonb");
        operation.HasIndex(x => x.NextAttemptAt);
        var claim = modelBuilder.Entity<SalesReturnClaim>();
        claim.ToTable("salesreturn_claims");
        claim.Property(x => x.Payload).HasColumnType("jsonb");
        claim.HasKey(x => new { x.AccountId, x.OldDocumentId });
        claim.HasOne<SalesReturnOperation>().WithMany()
            .HasForeignKey(x => new { x.AccountId, x.OperationId }).OnDelete(DeleteBehavior.Cascade);
    }
}
