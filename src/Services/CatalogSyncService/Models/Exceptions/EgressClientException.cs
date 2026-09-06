namespace MsContractor.CatalogSyncService.Models.Exceptions;

public sealed class EgressClientException(
    string code,
    string safeMessage,
    int statusCode) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
}
