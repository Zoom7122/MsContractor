namespace MsContractor.VendorService.Services.Exceptions;

public sealed class VendorConflictException(string message) : Exception(message);
