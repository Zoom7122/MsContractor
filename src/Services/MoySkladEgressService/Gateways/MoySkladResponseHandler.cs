using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace MsContractor.MoySkladEgressService.Gateways;

public interface IMoySkladResponseHandler
{
    Task<MoySkladResponseBody> ReadAsync(
        HttpResponseMessage response,
        MoySkladRequestContext context,
        TimeSpan duration,
        CancellationToken cancellationToken);

    EgressException ValidationFailure(
        MoySkladRequestContext context,
        int httpStatus,
        string validationError,
        string responseBody,
        TimeSpan duration);

    EgressException TransportFailure(
        MoySkladRequestContext context,
        string message,
        TimeSpan duration,
        Exception exception);
}

public sealed class MoySkladResponseHandler(ILogger<MoySkladResponseHandler> logger) : IMoySkladResponseHandler
{
    public const int MaximumDiagnosticBodyLength = 32 * 1024;
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "authorization", "jwt", "cookie", "set-cookie", "password", "secret",
        "vendorSecret", "token"
    };
    private static readonly Regex SensitiveText = new(
        @"(?ix)(bearer\s+)[a-z0-9._~+/=-]+|((?:access_token|authorization|jwt|cookie|password|secret|token)\s*[:=]\s*)(?:bearer\s+)?[^\s,;]+|\beyJ[a-z0-9_-]+\.eyJ[a-z0-9_-]+\.[a-z0-9_-]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public async Task<MoySkladResponseBody> ReadAsync(
        HttpResponseMessage response,
        MoySkladRequestContext context,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad response timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", exception);
        }

        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
            return new MoySkladResponseBody(body, status);

        var diagnosticBody = SanitizeAndTruncate(body);
        var (upstreamCode, upstreamMessage, structured) = ParseError(body);
        var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
        var errorCode = response.StatusCode == HttpStatusCode.TooManyRequests
            ? "MOYSKLAD_RATE_LIMITED"
            : structured ? "MOYSKLAD_HTTP_ERROR" : "MOYSKLAD_UNSTRUCTURED_ERROR_RESPONSE";
        var safeMessage = SanitizeText(structured
            ? upstreamMessage ?? "MoySklad rejected the request."
            : "MoySklad returned an unstructured error response.");
        upstreamMessage = upstreamMessage is null ? null : SanitizeText(upstreamMessage);

        LogFailure(
            retryable ? LogLevel.Warning : LogLevel.Error,
            context,
            status,
            errorCode,
            safeMessage,
            upstreamCode,
            upstreamMessage,
            null,
            diagnosticBody,
            retryable,
            duration);

        throw new EgressException(
            response.StatusCode == HttpStatusCode.TooManyRequests ? 429 : status >= 500 ? 503 : status,
            errorCode,
            safeMessage,
            httpStatus: status,
            retryable: retryable,
            moySkladErrorCode: upstreamCode,
            moySkladErrorMessage: upstreamMessage,
            sanitizedResponseBody: diagnosticBody);
    }

    public EgressException ValidationFailure(
        MoySkladRequestContext context,
        int httpStatus,
        string validationError,
        string responseBody,
        TimeSpan duration)
    {
        var diagnosticBody = SanitizeAndTruncate(responseBody);
        LogFailure(
            LogLevel.Error,
            context,
            httpStatus,
            "MOYSKLAD_RESPONSE_VALIDATION_FAILED",
            "MoySklad returned a response that did not satisfy the operation postcondition.",
            null,
            null,
            validationError,
            diagnosticBody,
            false,
            duration);
        return new EgressException(
            502,
            "MOYSKLAD_RESPONSE_VALIDATION_FAILED",
            validationError,
            httpStatus: httpStatus,
            retryable: false,
            sanitizedResponseBody: diagnosticBody,
            validationError: validationError);
    }

    public EgressException TransportFailure(
        MoySkladRequestContext context,
        string message,
        TimeSpan duration,
        Exception exception)
    {
        LogFailure(
            LogLevel.Warning,
            context,
            null,
            "MOYSKLAD_UNAVAILABLE",
            message,
            null,
            null,
            null,
            string.Empty,
            true,
            duration);
        return new EgressException(
            503,
            "MOYSKLAD_UNAVAILABLE",
            message,
            exception,
            retryable: true);
    }

    private void LogFailure(
        LogLevel level,
        MoySkladRequestContext context,
        int? httpStatus,
        string errorCode,
        string errorMessage,
        string? upstreamCode,
        string? upstreamMessage,
        string? validationError,
        string responseBody,
        bool retryable,
        TimeSpan duration) =>
        logger.Log(
            level,
            "MoySklad request failed: account_id={AccountId}, correlation_id={CorrelationId}, merge_job_id={MergeJobId}, merge_operation_id={MergeOperationId}, http_method={HttpMethod}, entity_type={EntityType}, entity_id={EntityId}, endpoint={Endpoint}, http_status={HttpStatus}, retry_attempt={RetryAttempt}, error_code={ErrorCode}, error_message={ErrorMessage}, moysklad_error_code={MoySkladErrorCode}, moysklad_error_message={MoySkladErrorMessage}, response_validation_error={ResponseValidationError}, retryable={Retryable}, duration_ms={DurationMs}, response_body={ResponseBody}",
            context.AccountId,
            context.CorrelationId,
            context.MergeJobId,
            context.MergeOperationId,
            context.HttpMethod,
            context.EntityType,
            context.EntityId,
            context.Endpoint,
            httpStatus,
            context.RetryAttempt,
            errorCode,
            errorMessage,
            upstreamCode,
            upstreamMessage,
            validationError,
            retryable,
            duration.TotalMilliseconds,
            responseBody);

    internal static (string? Code, string? Message, bool Structured) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null, false);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array)
                return (null, null, false);

            var parsed = errors.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => (
                    Code: ReadScalar(item, "code"),
                    Message: string.Join(", ", new[]
                    {
                        ReadScalar(item, "error"),
                        ReadScalar(item, "error_message"),
                        ReadScalar(item, "parameter") is { } parameter ? $"parameter={parameter}" : null
                    }.Where(value => !string.IsNullOrWhiteSpace(value)))))
                .Where(item => item.Code is not null || item.Message.Length > 0)
                .ToArray();
            if (parsed.Length == 0) return (null, null, false);
            return (
                string.Join(",", parsed.Select(item => item.Code).Where(value => value is not null)),
                string.Join(" | ", parsed.Select(item => item.Message).Where(value => value.Length > 0)),
                true);
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
    }

    internal static string SanitizeAndTruncate(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        string sanitized;
        try
        {
            var node = JsonNode.Parse(body);
            Sanitize(node);
            sanitized = node?.ToJsonString(DiagnosticJsonOptions) ?? string.Empty;
        }
        catch (JsonException)
        {
            sanitized = SanitizeText(body
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal));
        }
        return sanitized.Length <= MaximumDiagnosticBodyLength
            ? sanitized
            : sanitized[..MaximumDiagnosticBodyLength];
    }

    private static string SanitizeText(string value) => SensitiveText.Replace(
        value,
        match => match.Groups[1].Success
            ? $"{match.Groups[1].Value}[REDACTED]"
            : match.Groups[2].Success ? $"{match.Groups[2].Value}[REDACTED]" : "[REDACTED]");

    private static void Sanitize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (SensitiveNames.Contains(property.Key)) obj[property.Key] = "[REDACTED]";
                else if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[property.Key] = SanitizeText(text);
                else Sanitize(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[index] = SanitizeText(text);
                else Sanitize(array[index]);
            }
        }
    }

    private static string? ReadScalar(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}

internal sealed class ForwardingLogger<TSource, TTarget>(ILogger<TSource> logger) : ILogger<TTarget>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        logger.Log(logLevel, eventId, state, exception, formatter);
}
