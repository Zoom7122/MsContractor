using MsContractor.VendorService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MsContractor.VendorService.HealthChecks;

public sealed class VendorReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VendorDbContext>();
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL and Redis are available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Vendor dependencies are unavailable.", exception);
        }
    }
}
