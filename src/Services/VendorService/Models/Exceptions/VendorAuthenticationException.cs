namespace MsContractor.VendorService.Models.Exceptions;

public sealed class VendorAuthenticationException(string message = "Authentication failed.") : Exception(message);
