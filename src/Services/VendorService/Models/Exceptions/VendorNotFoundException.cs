namespace MsContractor.VendorService.Models.Exceptions;

public sealed class VendorNotFoundException(string message = "Installation was not found.") : Exception(message);
