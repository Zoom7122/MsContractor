using MsContractor.VendorService.Models.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MsContractor.VendorService.Services;

public sealed class MoyskladVendorJwtFactory
{
    private readonly IOptions<VendorOptions> _options;
    private readonly TimeProvider _timeProvider;

    public MoyskladVendorJwtFactory(
        IOptions<VendorOptions> options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Create()
    {
        var issuedAt = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "HS256", typ = "JWT" },
            JsonOptions));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                sub = _options.Value.AppUid,
                iat = issuedAt,
                exp = issuedAt + 300,
                jti = Guid.NewGuid().ToString("N")
            },
            JsonOptions));
        var unsignedToken = $"{header}.{payload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Value.SecretKey));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsignedToken)));
        return $"{unsignedToken}.{signature}";
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
