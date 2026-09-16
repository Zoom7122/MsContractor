using Microsoft.EntityFrameworkCore;

namespace MsContractor.MoySkladEgressService.Persistence;

public sealed class EgressDbContext(DbContextOptions<EgressDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("egress");
    }
}
