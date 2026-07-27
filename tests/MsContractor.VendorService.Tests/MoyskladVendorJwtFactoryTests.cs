using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MsContractor.VendorService.Services;

namespace MsContractor.VendorService.Tests;

public sealed class MoyskladVendorJwtFactoryTests
{
    [Fact]
    public void Create_ProducesValidOneTimeHs256Token()
    {
        var now = DateTimeOffset.Parse("2026-07-27T12:00:00Z");
        var factory = new MoyskladVendorJwtFactory(
            TestSupport.Options(),
            new MutableTimeProvider(now));

        var first = factory.Create();
        var second = factory.Create();
        var firstParts = first.Split('.');
        var secondParts = second.Split('.');
        using var payload = JsonDocument.Parse(Decode(firstParts[1]));
        using var secondPayload = JsonDocument.Parse(Decode(secondParts[1]));

        Assert.Equal("HS256", JsonDocument.Parse(Decode(firstParts[0])).RootElement.GetProperty("alg").GetString());
        Assert.Equal(TestSupport.AppUid, payload.RootElement.GetProperty("sub").GetString());
        Assert.Equal(now.ToUnixTimeSeconds(), payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(300, payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64());
        Assert.NotEqual(
            payload.RootElement.GetProperty("jti").GetString(),
            secondPayload.RootElement.GetProperty("jti").GetString());

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestSupport.SecretKey));
        var expectedSignature = hmac.ComputeHash(Encoding.ASCII.GetBytes($"{firstParts[0]}.{firstParts[1]}"));
        Assert.Equal(expectedSignature, Decode(firstParts[2]));
    }

    private static byte[] Decode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
