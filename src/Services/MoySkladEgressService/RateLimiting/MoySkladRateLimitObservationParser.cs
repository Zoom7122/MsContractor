using MsContractor.MoySkladEgressService.RateLimiting.Models;

namespace MsContractor.MoySkladEgressService.RateLimiting;

public static class MoySkladRateLimitObservationParser
{
    public static MoySkladRateLimitObservation Parse(
        HttpResponseMessage response)
    {
        return new MoySkladRateLimitObservation(
            Limit: GetInt(response, "X-RateLimit-Limit"),
            Remaining: GetInt(response, "X-RateLimit-Remaining"),
            IntervalMs: GetLong(response, "X-Lognex-Retry-TimeInterval"),
            RetryAfterMs: GetLong(response, "X-Lognex-Retry-After"),
            ResetMs: GetLong(response, "X-Lognex-Reset"),
            IsRateLimited: response.StatusCode ==
                System.Net.HttpStatusCode.TooManyRequests);
    }

    private static int? GetInt(
        HttpResponseMessage response,
        string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;

        return int.TryParse(values.FirstOrDefault(), out var value)
            ? value
            : null;
    }

    private static long? GetLong(
        HttpResponseMessage response,
        string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return null;

        return long.TryParse(values.FirstOrDefault(), out var value)
            ? value
            : null;
    }
}
