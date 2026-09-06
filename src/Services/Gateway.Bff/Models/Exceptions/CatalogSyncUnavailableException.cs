namespace MsContractor.Gateway.Bff.Models.Exceptions;

public sealed class CatalogSyncUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
