using MsContractor.VendorService.Repositories;
using MsContractor.VendorService.Models;
using MsContractor.VendorService.Models.Options;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MsContractor.VendorService.Services;

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
    IVendorSessionRepository repository,
    IOptions<VendorOptions> options,
    TimeProvider timeProvider) : IVendorSessionStore
{
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
        await repository.CreateAsync(tokenHash, session, SessionLifetime, cancellationToken);

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
        var session = await repository.ReadAsync(tokenHash, cancellationToken);
        if (session is null)
            return null;

        var refreshed = session with { LastActivityAt = timeProvider.GetUtcNow() };
        await repository.RefreshAsync(tokenHash, refreshed, SessionLifetime, cancellationToken);

        return refreshed;
    }

    public async Task DeleteAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token))
            return;

        await repository.DeleteAsync(HashToken(token), cancellationToken);
    }

    public Task RevokeAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        repository.RevokeAccountAsync(accountId, cancellationToken);

    private static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
