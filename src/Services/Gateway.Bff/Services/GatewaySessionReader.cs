using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace MsContractor.Gateway.Bff.Services;

public sealed record GatewaySession(Guid AccountId, Guid EmployeeId);

public interface IGatewaySessionReader
{
    Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken);
}

public sealed class GatewaySessionReader(IConnectionMultiplexer redis) : IGatewaySessionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var value = await redis.GetDatabase().StringGetAsync($"vendor:session:{hash}");
        if (value.IsNullOrEmpty)
            return null;

        try
        {
            var session = JsonSerializer.Deserialize<GatewaySession>(value.ToString(), JsonOptions);
            return session is null || session.AccountId == Guid.Empty || session.EmployeeId == Guid.Empty
                ? null
                : session;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
