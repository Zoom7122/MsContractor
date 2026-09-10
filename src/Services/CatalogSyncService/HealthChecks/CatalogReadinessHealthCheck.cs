using MsContractor.CatalogSyncService.Persistence;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MsContractor.CatalogSyncService.HealthChecks;

public sealed class CatalogReadinessHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAdminClient _adminClient;

    public CatalogReadinessHealthCheck(
        IServiceScopeFactory scopeFactory,
        IAdminClient adminClient)
    {
        _scopeFactory = scopeFactory;
        _adminClient = adminClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            var metadata = _adminClient.GetMetadata(TimeSpan.FromSeconds(3));
            if (metadata.Brokers.Count == 0)
                return HealthCheckResult.Unhealthy("Kafka returned no brokers.");
            return HealthCheckResult.Healthy("PostgreSQL and Kafka are available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Catalog dependencies are unavailable.", exception);
        }
    }
}
