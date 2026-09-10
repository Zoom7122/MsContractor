using MsContractor.VendorService.Repositories;
using MsContractor.VendorService.Models;
using MsContractor.VendorService.Models.Exceptions;

namespace MsContractor.VendorService.Services;

public sealed class VendorJwtReplayStore
{
    private readonly IVendorJwtReplayRepository _repository;
    private readonly TimeProvider _timeProvider;

    public VendorJwtReplayStore(
        IVendorJwtReplayRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async Task EnsureUnusedAsync(VendorJwt jwt, CancellationToken cancellationToken)
    {
        var ttl = jwt.ExpiresAt - _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        if (ttl <= TimeSpan.Zero)
            throw new VendorAuthenticationException();
        var stored = await _repository.TryUseAsync(jwt.Jti, ttl, cancellationToken);
        if (!stored)
            throw new VendorAuthenticationException();
    }
}
