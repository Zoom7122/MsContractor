namespace DocumentRelationsGenerator.MoySklad;

/// <summary>
/// API failure with only the data that is safe to print: method, path, HTTP status and the documented
/// MoySklad error code/message. Headers and credentials never reach this type.
/// </summary>
public sealed class MoySkladApiException : Exception
{
    public MoySkladApiException(string method, string path, int? statusCode, int? errorCode, string? errorMessage,
        bool outcomeUnknown = false)
        : base(Describe(method, path, statusCode, errorCode, errorMessage, outcomeUnknown))
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        OutcomeUnknown = outcomeUnknown;
    }

    public string Method { get; }
    public string Path { get; }
    public int? StatusCode { get; }
    public int? ErrorCode { get; }
    public string? ErrorMessage { get; }

    /// <summary>A create request failed in transport and was not retried, so the object may exist.</summary>
    public bool OutcomeUnknown { get; }

    public bool IsAuthenticationFailure => StatusCode is 401 or 403;

    private static string Describe(string method, string path, int? statusCode, int? errorCode, string? errorMessage,
        bool outcomeUnknown)
    {
        var status = statusCode is null ? "no HTTP response" : $"HTTP {statusCode}";
        var code = errorCode is null ? "" : $", code {errorCode}";
        var message = string.IsNullOrEmpty(errorMessage) ? "" : $": {errorMessage}";
        var unknown = outcomeUnknown ? " (outcome unknown, request was not repeated)" : "";
        return $"{method} {path} failed with {status}{code}{message}{unknown}";
    }
}
