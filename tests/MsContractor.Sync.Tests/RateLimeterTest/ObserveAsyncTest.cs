using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.RateLimiting;
public class MoySkladRateLimiterTests
{
    [Fact]
    public async Task ObserveAsync_ShouldWriteRateLimitToRedis()
    {
        var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

        var limiter = new MoySkladRateLimiter(
            redis,
            NullLogger<MoySkladRateLimiter>.Instance);

        var accountId = Guid.NewGuid();

        var observation = new MoySkladRateLimitObservation(
            Limit: 15,
            Remaining: 12,
            IntervalMs: 3000,
            ResetMs: 0,
            RetryAfterMs: 0,
            IsRateLimited: false);

        await limiter.ObserveAsync(
            accountId,
            observation,
            CancellationToken.None);

        var database = redis.GetDatabase();

        var key = $"RateLimits:{{{accountId:D}}}";

        var values = await database.HashGetAllAsync(key);
    }
}
