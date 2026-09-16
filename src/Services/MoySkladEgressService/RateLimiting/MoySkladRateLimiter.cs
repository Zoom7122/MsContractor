using System.Collections.Concurrent;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using StackExchange.Redis;

namespace MsContractor.MoySkladEgressService.RateLimiting;

public sealed class MoySkladRateLimiter : IMoySkladRateLimiter
{
    private const long DefaultIntervalMs = 3000;
    private const double SafetyMultiplier = 1.10;
    private readonly ILogger<MoySkladRateLimiter> _logger;
    private readonly IConnectionMultiplexer _redis;

    private const long RemainingReserve = 3;

    public MoySkladRateLimiter(
        IConnectionMultiplexer redis,
        ILogger<MoySkladRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;
    }


public async Task WaitAsync(
    Guid accountId,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    var database = _redis.GetDatabase();
    var key = $"RateLimits:{{{accountId:D}}}";

    var values = await database.HashGetAsync(
        key,
        new RedisValue[]
        {
            "remaining_observed",
            "interval_ms",
            "reset_ms",
            "retry_after_ms",
            "updated_at_ms"
        });

    // Лимитов в Redis ещё нет.
    // Первый запрос разрешаем.
    if (values.All(x => x.IsNull))
        return;

    var remaining = (long?)values[0] ?? 0;
    var intervalMs = (long?)values[1] ?? DefaultIntervalMs;
    var resetMs = (long?)values[2] ?? 0;
    var retryAfterMs = (long?)values[3] ?? 0;
    var updatedAtMs = (long?)values[4] ?? 0;

    // Запас ещё есть.
    if (remaining > RemainingReserve)
        return;

    var waitIntervalMs =
        retryAfterMs > 0
            ? retryAfterMs
            : resetMs > 0
                ? resetMs
                : intervalMs;

    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    var allowedAtMs = updatedAtMs + waitIntervalMs;
    var waitMs = allowedAtMs - nowMs;

    if (waitMs > 0)
    {
        _logger.LogInformation(
            "MoySklad rate limit reserve reached. account_id={AccountId}, remaining={Remaining}, wait_ms={WaitMs}",
            accountId,
            remaining,
            waitMs);

        await Task.Delay(
            TimeSpan.FromMilliseconds(waitMs),
            cancellationToken);
    }

    // Считаем старые данные лимита протухшими.
    await database.KeyDeleteAsync(key);
}
   public async Task ObserveAsync(
    Guid accountId,
    MoySkladRateLimitObservation observation,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    var database = _redis.GetDatabase();
    var key = $"RateLimits:{{{accountId:D}}}";

    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    var intervalMs =
        observation.IntervalMs ?? DefaultIntervalMs;

    await database.HashSetAsync(
        key,
        new HashEntry[]
        {
            new("limit", observation.Limit ?? 0),
            new("remaining_observed", observation.Remaining ?? 0),
            new("interval_ms", intervalMs),
            new("reset_ms", observation.ResetMs ?? 0),
            new("retry_after_ms", observation.RetryAfterMs ?? 0),
            new("updated_at_ms", nowMs)
        });

    await database.KeyExpireAsync(
        key,
        TimeSpan.FromMilliseconds(intervalMs));
}
}
