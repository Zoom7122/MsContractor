namespace MsContractor.VendorService.Models.Exceptions;

public sealed class VendorValidationException(string message) : Exception(message);
