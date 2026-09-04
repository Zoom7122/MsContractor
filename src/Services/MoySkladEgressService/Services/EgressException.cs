namespace MsContractor.MoySkladEgressService.Services;

public sealed class EgressException(
    int statusCode,
    string code,
    string safeMessage,
    Exception? innerException = null,
    int? httpStatus = null,
    bool? retryable = null,
    string? moySkladErrorCode = null,
    string? moySkladErrorMessage = null,
    string? sanitizedResponseBody = null,
    string? validationError = null) : Exception(safeMessage, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int? HttpStatus { get; } = httpStatus;
    public bool Retryable { get; } = retryable ?? statusCode == 429 || statusCode >= 500;
    public string? MoySkladErrorCode { get; } = moySkladErrorCode;
    public string? MoySkladErrorMessage { get; } = moySkladErrorMessage;
    public string? SanitizedResponseBody { get; } = sanitizedResponseBody;
    public string? ValidationError { get; } = validationError;
}
