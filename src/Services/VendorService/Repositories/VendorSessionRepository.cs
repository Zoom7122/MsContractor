using System.Text.Json;
using StackExchange.Redis;
using MsContractor.VendorService.Models;

namespace MsContractor.VendorService.Repositories;

public interface IVendorSessionRepository
{
    Task<VendorSession?> ReadAsync(string tokenHash, CancellationToken cancellationToken);
    Task CreateAsync(string tokenHash, VendorSession session, TimeSpan lifetime, CancellationToken cancellationToken);
    Task RefreshAsync(string tokenHash, VendorSession session, TimeSpan lifetime, CancellationToken cancellationToken);
    Task DeleteAsync(string tokenHash, CancellationToken cancellationToken);
    Task RevokeAccountAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class VendorSessionRepository(IConnectionMultiplexer redis) : IVendorSessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string SessionKey(string hash) => $"vendor:session:{hash}";
    private static string AccountKey(Guid accountId) => $"vendor:account-sessions:{accountId:D}";

    public async Task<VendorSession?> ReadAsync(string tokenHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        var value = await database.StringGetAsync(SessionKey(tokenHash));
        if (value.IsNullOrEmpty) return null;
        try
        {
            return JsonSerializer.Deserialize<VendorSession>(value.ToString(), JsonOptions)
                ?? throw new JsonException("Session is empty.");
        }
        catch (JsonException)
        {
            await database.KeyDeleteAsync(SessionKey(tokenHash));
            return null;
        }
    }

    public Task CreateAsync(string tokenHash, VendorSession session, TimeSpan lifetime, CancellationToken cancellationToken) =>
        WriteAsync(tokenHash, session, lifetime, "Could not create vendor session.", cancellationToken);

    public Task RefreshAsync(string tokenHash, VendorSession session, TimeSpan lifetime, CancellationToken cancellationToken) =>
        WriteAsync(tokenHash, session, lifetime, "Could not refresh vendor session.", cancellationToken);

    private async Task WriteAsync(string tokenHash, VendorSession session, TimeSpan lifetime, string errorMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = redis.GetDatabase().CreateTransaction();
        _ = transaction.StringSetAsync(SessionKey(tokenHash), JsonSerializer.Serialize(session, JsonOptions), lifetime);
        _ = transaction.SetAddAsync(AccountKey(session.AccountId), tokenHash);
        _ = transaction.KeyExpireAsync(AccountKey(session.AccountId), lifetime);
        if (!await transaction.ExecuteAsync()) throw new RedisException(errorMessage);
    }

    public async Task DeleteAsync(string tokenHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        var value = await database.StringGetAsync(SessionKey(tokenHash));
        if (!value.IsNullOrEmpty)
        {
            try
            {
                var session = JsonSerializer.Deserialize<VendorSession>(value.ToString(), JsonOptions);
                if (session is not null) await database.SetRemoveAsync(AccountKey(session.AccountId), tokenHash);
            }
            catch (JsonException) { }
        }
        await database.KeyDeleteAsync(SessionKey(tokenHash));
    }

    public async Task RevokeAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        var accountKey = AccountKey(accountId);
        var hashes = await database.SetMembersAsync(accountKey);
        var keys = hashes.Where(x => !x.IsNullOrEmpty).Select(x => (RedisKey)SessionKey(x!)).Append(accountKey).ToArray();
        if (keys.Length > 0) await database.KeyDeleteAsync(keys);
    }
}
