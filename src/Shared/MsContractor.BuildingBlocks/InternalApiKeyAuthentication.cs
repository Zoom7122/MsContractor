using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;

namespace MsContractor.BuildingBlocks.Security;

public static class InternalApiKeyAuthentication
{
    public static bool IsAuthorized(HttpRequest request, IConfiguration configuration)
    {
        var expected = configuration["InternalApi:Key"];
        var supplied = request.Headers[InternalApiHeaders.ApiKey].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
