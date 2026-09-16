using MsContractor.MoySkladEgressService.RateLimiting.Models;

namespace MsContractor.MoySkladEgressService.RateLimiting;

public interface IMoySkladRateLimiter
{
    Task WaitAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task ObserveAsync(
        Guid accountId,
        MoySkladRateLimitObservation observation,
        CancellationToken cancellationToken);
}
