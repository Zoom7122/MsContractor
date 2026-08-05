namespace MsContractor.MoySkladEgressService.Services;

public sealed class EgressException(
    int statusCode,
    string code,
    string safeMessage,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}
