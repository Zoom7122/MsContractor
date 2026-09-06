namespace MsContractor.DuplicatesMergeService.Models.Exceptions;

public sealed class MergeEgressException(
    string code,
    string safeMessage,
    int statusCode) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
    public bool IsRetryable => StatusCode == 429 || StatusCode >= 500;
}
