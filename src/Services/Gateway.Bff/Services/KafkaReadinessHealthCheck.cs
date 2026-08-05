using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace MsContractor.Gateway.Bff.Services;

public sealed class KafkaReadinessHealthCheck(
    IAdminClient adminClient,
    IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(3));
            return metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy("Redis and Kafka are available.")
                : HealthCheckResult.Unhealthy("Kafka returned no brokers.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Gateway dependencies are unavailable.", exception);
        }
    }
}
