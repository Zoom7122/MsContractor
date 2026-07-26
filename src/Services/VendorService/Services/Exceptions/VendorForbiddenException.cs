namespace MsContractor.VendorService.Services.Exceptions;

public sealed class VendorForbiddenException(string message = "Application is not authorized.") : Exception(message);
