namespace MsContractor.VendorService.Services.Exceptions;

public sealed class VendorValidationException(string message) : Exception(message);
