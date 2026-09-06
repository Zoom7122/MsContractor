namespace MsContractor.DuplicatesMergeService.Models.Exceptions;

public sealed class MergeRetryableException(string message, Exception innerException) : Exception(message, innerException);
