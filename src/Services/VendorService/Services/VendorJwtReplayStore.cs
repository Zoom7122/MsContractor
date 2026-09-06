using MsContractor.VendorService.Repositories;
using MsContractor.VendorService.Models;
using MsContractor.VendorService.Models.Exceptions;

namespace MsContractor.VendorService.Services;

public sealed class VendorJwtReplayStore(IVendorJwtReplayRepository repository, TimeProvider timeProvider)
{
    public async Task EnsureUnusedAsync(VendorJwt jwt, CancellationToken cancellationToken)
    {
        var ttl = jwt.ExpiresAt - timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        if (ttl <= TimeSpan.Zero)
            throw new VendorAuthenticationException();
        var stored = await repository.TryUseAsync(jwt.Jti, ttl, cancellationToken);
        if (!stored)
            throw new VendorAuthenticationException();
    }
}
