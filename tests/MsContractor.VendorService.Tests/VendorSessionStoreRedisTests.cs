using System.Security.Cryptography;
using System.Text;
using MsContractor.VendorService.Services;
using StackExchange.Redis;

namespace MsContractor.VendorService.Tests;

public sealed class VendorSessionStoreRedisTests
{
    [Fact]
    public async Task Sessions_AreHashedRefreshedAndRevokedPerAccount()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var accountId = Guid.NewGuid();
        var firstEmployee = Guid.NewGuid();
        var secondEmployee = Guid.NewGuid();
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
        var store = new VendorSessionStore(
            redis,
            TestSupport.Options(TimeSpan.FromMinutes(2)),
            timeProvider);

        var firstToken = await store.CreateAsync(accountId, firstEmployee, CancellationToken.None);
        var secondToken = await store.CreateAsync(accountId, secondEmployee, CancellationToken.None);
        var firstHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(firstToken)));
        var database = redis.GetDatabase();

        Assert.False(await database.KeyExistsAsync($"vendor:session:{firstToken}"));
        Assert.True(await database.KeyExistsAsync($"vendor:session:{firstHash}"));

        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(1);
        var refreshed = await store.GetAndRefreshAsync(firstToken, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Equal(firstEmployee, refreshed!.EmployeeId);
        Assert.Equal(timeProvider.UtcNow, refreshed.LastActivityAt);

        await store.RevokeAccountAsync(accountId, CancellationToken.None);

        Assert.Null(await store.GetAndRefreshAsync(firstToken, CancellationToken.None));
        Assert.Null(await store.GetAndRefreshAsync(secondToken, CancellationToken.None));
    }
}
