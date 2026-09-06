namespace MsContractor.VendorService.Models.Exceptions;

public sealed class VendorForbiddenException(string message = "Application is not authorized.") : Exception(message);
