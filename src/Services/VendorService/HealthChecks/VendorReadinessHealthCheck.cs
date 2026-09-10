using MsContractor.VendorService.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MsContractor.VendorService.HealthChecks;

public sealed class VendorReadinessHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;

    public VendorReadinessHealthCheck(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<VendorDbContext>();
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
            await _redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL and Redis are available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Vendor dependencies are unavailable.", exception);
        }
    }
}
