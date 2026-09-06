using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MsContractor.MoySkladEgressService.Persistence;

public sealed class EgressDbContextFactory : IDesignTimeDbContextFactory<EgressDbContext>
{
    public EgressDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<EgressDbContext>().UseNpgsql(
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=mscontractor;Username=postgres",
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "egress")).Options);
}
