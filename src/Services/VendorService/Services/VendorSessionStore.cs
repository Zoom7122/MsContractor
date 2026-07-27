using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MsContractor.VendorService.Services;

public sealed record VendorSession(
    Guid AccountId,
    Guid EmployeeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

public interface IVendorSessionStore
{
    Task<string> CreateAsync(
        Guid accountId,
        Guid employeeId,
        CancellationToken cancellationToken);

    Task<VendorSession?> GetAndRefreshAsync(
        string token,
        CancellationToken cancellationToken);

    Task DeleteAsync(string token, CancellationToken cancellationToken);

    Task RevokeAccountAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class VendorSessionStore(
    IConnectionMultiplexer redis,
    IOptions<VendorOptions> options,
    TimeProvider timeProvider) : IVendorSessionStore
{
    private const string SessionKeyPrefix = "vendor:session:";
    private const string AccountSessionsKeyPrefix = "vendor:account-sessions:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private TimeSpan SessionLifetime => options.Value.SessionLifetime;

    public async Task<string> CreateAsync(
        Guid accountId,
        Guid employeeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(token);
        var now = timeProvider.GetUtcNow();
        var session = new VendorSession(accountId, employeeId, now, now);
        var database = redis.GetDatabase();
        var transaction = database.CreateTransaction();

        _ = transaction.StringSetAsync(
            SessionKey(tokenHash),
            JsonSerializer.Serialize(session, JsonOptions),
            SessionLifetime);
        _ = transaction.SetAddAsync(AccountSessionsKey(accountId), tokenHash);
        _ = transaction.KeyExpireAsync(AccountSessionsKey(accountId), SessionLifetime);

        if (!await transaction.ExecuteAsync())
            throw new RedisException("Could not create vendor session.");

        return token;
    }

    public async Task<VendorSession?> GetAndRefreshAsync(
        string token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var tokenHash = HashToken(token);
        var database = redis.GetDatabase();
        var value = await database.StringGetAsync(SessionKey(tokenHash));
        if (value.IsNullOrEmpty)
            return null;

        VendorSession session;
        try
        {
            session = JsonSerializer.Deserialize<VendorSession>(value.ToString(), JsonOptions)
                ?? throw new JsonException("Session is empty.");
        }
        catch (JsonException)
        {
            await database.KeyDeleteAsync(SessionKey(tokenHash));
            return null;
        }

        var refreshed = session with { LastActivityAt = timeProvider.GetUtcNow() };
        var transaction = database.CreateTransaction();
        _ = transaction.StringSetAsync(
            SessionKey(tokenHash),
            JsonSerializer.Serialize(refreshed, JsonOptions),
            SessionLifetime);
        _ = transaction.SetAddAsync(AccountSessionsKey(refreshed.AccountId), tokenHash);
        _ = transaction.KeyExpireAsync(AccountSessionsKey(refreshed.AccountId), SessionLifetime);

        if (!await transaction.ExecuteAsync())
            throw new RedisException("Could not refresh vendor session.");

        return refreshed;
    }

    public async Task DeleteAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return;

        var tokenHash = HashToken(token);
        var database = redis.GetDatabase();
        var value = await database.StringGetAsync(SessionKey(tokenHash));
        if (!value.IsNullOrEmpty)
        {
            try
            {
                var session = JsonSerializer.Deserialize<VendorSession>(value.ToString(), JsonOptions);
                if (session is not null)
                    await database.SetRemoveAsync(AccountSessionsKey(session.AccountId), tokenHash);
            }
            catch (JsonException)
            {
                // The invalid session is still deleted below.
            }
        }

        await database.KeyDeleteAsync(SessionKey(tokenHash));
    }

    public async Task RevokeAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        var accountKey = AccountSessionsKey(accountId);
        var tokenHashes = await database.SetMembersAsync(accountKey);
        var keys = tokenHashes
            .Where(value => !value.IsNullOrEmpty)
            .Select(value => (RedisKey)SessionKey(value!))
            .Append(accountKey)
            .ToArray();
        if (keys.Length > 0)
            await database.KeyDeleteAsync(keys);
    }

    private static string SessionKey(string tokenHash) => $"{SessionKeyPrefix}{tokenHash}";

    private static string AccountSessionsKey(Guid accountId) =>
        $"{AccountSessionsKeyPrefix}{accountId:D}";

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
