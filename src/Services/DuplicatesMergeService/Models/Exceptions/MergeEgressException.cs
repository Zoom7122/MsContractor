namespace MsContractor.DuplicatesMergeService.Models.Exceptions;

public sealed class MergeEgressException(
    string code,
    string safeMessage,
    int statusCode) : Exception(safeMessage)
{
    public const string PurchaseReturnUnknownResult = "PURCHASERETURN_RECREATION_UNKNOWN_RESULT";

    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
    public bool IsRetryable => Code != PurchaseReturnUnknownResult && (StatusCode == 429 || StatusCode >= 500);
}
