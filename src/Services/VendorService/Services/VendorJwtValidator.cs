using MsContractor.VendorService.Models;
using MsContractor.VendorService.Models.Exceptions;
using MsContractor.VendorService.Models.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MsContractor.VendorService.Services;

public sealed class VendorJwtValidator(IOptions<VendorOptions> options, TimeProvider timeProvider)
{
    public VendorJwt Validate(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length <= 7)
            throw new VendorAuthenticationException();

        var parts = authorization[7..].Split('.');
        if (parts.Length != 3)
            throw new VendorAuthenticationException();

        try
        {
            var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "HS256")
                throw new VendorAuthenticationException();

            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.SecretKey));
            var actual = Base64UrlDecode(parts[2]);
            if (!CryptographicOperations.FixedTimeEquals(hmac.ComputeHash(signed), actual))
                throw new VendorAuthenticationException();

            var payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
            if (!payload.TryGetProperty("iat", out var iat) || !iat.TryGetInt64(out _) ||
                !payload.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds) ||
                !payload.TryGetProperty("jti", out var jti) || string.IsNullOrWhiteSpace(jti.GetString()))
                throw new VendorAuthenticationException();

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            if (expiresAt < timeProvider.GetUtcNow().AddSeconds(-30))
                throw new VendorAuthenticationException();
            return new VendorJwt(jti.GetString()!, expiresAt);
        }
        catch (VendorAuthenticationException) { throw; }
        catch (Exception) { throw new VendorAuthenticationException(); }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
