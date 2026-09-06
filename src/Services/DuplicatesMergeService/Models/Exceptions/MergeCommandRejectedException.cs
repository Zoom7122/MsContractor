namespace MsContractor.DuplicatesMergeService.Models.Exceptions;

public sealed class MergeCommandRejectedException(string message) : Exception(message);
