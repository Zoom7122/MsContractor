using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MsContractor.Gateway.Bff.Repositories;
using MsContractor.Gateway.Bff.Services;
using StackExchange.Redis;

namespace MsContractor.Sync.Tests;

public sealed class GatewaySessionRepositoryTests
{
    [Fact]
    public async Task SessionReader_UsesHashedRedisKeyAndRejectsMalformedSessions()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var token = Guid.NewGuid().ToString("N");
        var key = "vendor:session:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var database = redis.GetDatabase();
        var reader = new GatewaySessionReader(new GatewaySessionRepository(redis));
        var accountId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        try
        {
            Assert.Null(await reader.ReadAsync(token, CancellationToken.None));
            await database.StringSetAsync(key, JsonSerializer.Serialize(new { accountId, employeeId }), TimeSpan.FromMinutes(1));
            var session = await reader.ReadAsync(token, CancellationToken.None);
            Assert.Equal(accountId, session!.AccountId);
            Assert.Equal(employeeId, session.EmployeeId);
            await database.StringSetAsync(key, "not json", TimeSpan.FromMinutes(1));
            Assert.Null(await reader.ReadAsync(token, CancellationToken.None));
            await database.StringSetAsync(key, "{}", TimeSpan.FromMinutes(1));
            Assert.Null(await reader.ReadAsync(token, CancellationToken.None));
            await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(token, new CancellationToken(true)));
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }
}
