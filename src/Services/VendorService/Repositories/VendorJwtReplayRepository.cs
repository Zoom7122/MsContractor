using StackExchange.Redis;

namespace MsContractor.VendorService.Repositories;

public interface IVendorJwtReplayRepository
{
    Task<bool> TryUseAsync(string jti, TimeSpan lifetime, CancellationToken cancellationToken);
}

public sealed class VendorJwtReplayRepository(IConnectionMultiplexer redis) : IVendorJwtReplayRepository
{
    public Task<bool> TryUseAsync(string jti, TimeSpan lifetime, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync($"vendor:jwt:jti:{jti}", "1", lifetime, When.NotExists).WaitAsync(cancellationToken);
}
