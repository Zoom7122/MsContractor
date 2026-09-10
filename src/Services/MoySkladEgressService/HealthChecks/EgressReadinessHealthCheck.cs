using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.HealthChecks;

public sealed class EgressReadinessHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;
    private readonly EgressDbContext _db;

    public EgressReadinessHealthCheck(
        IConnectionMultiplexer redis,
        EgressDbContext db)
    {
        _redis = redis;
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            if (!await _db.Database.CanConnectAsync(cancellationToken) ||
                (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy("Egress operation journal is unavailable or requires migration.");
            return HealthCheckResult.Healthy("Redis and PostgreSQL are available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Egress dependencies are unavailable.", exception);
        }
    }
}
