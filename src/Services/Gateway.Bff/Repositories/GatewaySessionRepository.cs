using StackExchange.Redis;

namespace MsContractor.Gateway.Bff.Repositories;

public interface IGatewaySessionRepository
{
    Task<string?> ReadAsync(string tokenHash, CancellationToken cancellationToken);
}

public sealed class GatewaySessionRepository : IGatewaySessionRepository
{
    private readonly IConnectionMultiplexer _redis;

    public GatewaySessionRepository(
        IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<string?> ReadAsync(string tokenHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _redis.GetDatabase().StringGetAsync($"vendor:session:{tokenHash}");
        return value.IsNullOrEmpty ? null : value.ToString();
    }
}
