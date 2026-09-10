using MsContractor.Gateway.Bff.Repositories;
using MsContractor.Gateway.Bff.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MsContractor.Gateway.Bff.Services;

public interface IGatewaySessionReader
{
    Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken);
}

public sealed class GatewaySessionReader : IGatewaySessionReader
{
    private readonly IGatewaySessionRepository _repository;

    public GatewaySessionReader(IGatewaySessionRepository repository)
    {
        _repository = repository;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var value = await _repository.ReadAsync(hash, cancellationToken);
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            var session = JsonSerializer.Deserialize<GatewaySession>(value, JsonOptions);
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
