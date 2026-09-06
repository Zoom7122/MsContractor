using StackExchange.Redis;

namespace MsContractor.Gateway.Bff.Repositories;

public interface IGatewaySessionRepository
{
    Task<string?> ReadAsync(string tokenHash, CancellationToken cancellationToken);
}

public sealed class GatewaySessionRepository(IConnectionMultiplexer redis) : IGatewaySessionRepository
{
    public async Task<string?> ReadAsync(string tokenHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await redis.GetDatabase().StringGetAsync($"vendor:session:{tokenHash}");
        return value.IsNullOrEmpty ? null : value.ToString();
    }
}
