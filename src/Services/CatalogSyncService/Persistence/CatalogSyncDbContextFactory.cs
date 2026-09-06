using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MsContractor.CatalogSyncService.Persistence;

public sealed class CatalogSyncDbContextFactory : IDesignTimeDbContextFactory<CatalogSyncDbContext>
{
    public CatalogSyncDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CatalogSyncDbContext>().UseNpgsql(
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=mscontractor;Username=postgres").Options);
}
