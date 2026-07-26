using StackExchange.Redis;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Services;

public sealed class VendorJwtReplayStore(IConnectionMultiplexer redis, TimeProvider timeProvider)
{
    public async Task EnsureUnusedAsync(VendorJwt jwt, CancellationToken cancellationToken)
    {
        var ttl = jwt.ExpiresAt - timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        if (ttl <= TimeSpan.Zero)
            throw new VendorAuthenticationException();
        var stored = await redis.GetDatabase().StringSetAsync(
            $"vendor:jwt:jti:{jwt.Jti}", "1", ttl, When.NotExists).WaitAsync(cancellationToken);
        if (!stored)
            throw new VendorAuthenticationException();
    }
}
