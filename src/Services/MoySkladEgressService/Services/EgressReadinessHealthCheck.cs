using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MsContractor.MoySkladEgressService.Services;

public sealed class EgressReadinessHealthCheck(
    IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy("Redis is available.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Egress dependencies are unavailable.", exception);
        }
    }
}
