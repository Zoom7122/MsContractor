namespace MsContractor.VendorService.Services.Exceptions;

public sealed class VendorNotFoundException(string message = "Installation was not found.") : Exception(message);
