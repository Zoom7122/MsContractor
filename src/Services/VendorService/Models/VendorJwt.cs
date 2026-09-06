namespace MsContractor.VendorService.Models;

public sealed record VendorJwt(string Jti, DateTimeOffset ExpiresAt);
