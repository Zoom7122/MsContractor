using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.HealthChecks;

public sealed class EgressReadinessHealthCheck(
    IConnectionMultiplexer redis, EgressDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken) ||
                (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy("Egress operation journal is unavailable or requires migration.");
            return HealthCheckResult.Healthy("Redis and PostgreSQL are available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Egress dependencies are unavailable.", exception);
        }
    }
}
