

namespace MsContractor.MoySkladEgressService.RateLimiting.Models;

public sealed record MoySkladRateLimitObservation(
    int? Limit,
    int? Remaining,
    long? IntervalMs,
    long? RetryAfterMs,
    long? ResetMs,
    bool IsRateLimited);
