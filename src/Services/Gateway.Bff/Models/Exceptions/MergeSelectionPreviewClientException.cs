namespace MsContractor.Gateway.Bff.Models.Exceptions;

public sealed class MergeSelectionPreviewClientException(
    int statusCode,
    string code,
    string safeMessage,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}
