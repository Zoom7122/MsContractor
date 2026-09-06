namespace MsContractor.VendorService.Models.Exceptions;

public sealed class VendorConflictException(string message) : Exception(message);
