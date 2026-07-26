namespace MsContractor.VendorService.Services.Exceptions;

public sealed class VendorAuthenticationException(string message = "Authentication failed.") : Exception(message);
