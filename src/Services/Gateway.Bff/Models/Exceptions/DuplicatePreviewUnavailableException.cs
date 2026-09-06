namespace MsContractor.Gateway.Bff.Models.Exceptions;

public sealed class DuplicatePreviewUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
