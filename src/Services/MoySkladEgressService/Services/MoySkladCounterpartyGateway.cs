using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http.Json;
using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Services;

public sealed record MoySkladRawResponse(
    string Json,
    int StatusCode,
    string? ContentType,
    IReadOnlyDictionary<string, string> SafeHeaders);

public interface IMoySkladCounterpartyGateway
{
    Task<MoySkladRawResponse> GetAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<MoySkladRawResponse> UpdateAsync(
        Guid accountId, Guid counterpartyId, InternalCounterpartyUpdateRequest update,
        Guid mergeJobId, Guid operationId, Guid userId, string correlationId,
        CancellationToken cancellationToken);

    Task<MoySkladRawResponse> ArchiveAsync(
        Guid accountId, Guid counterpartyId, Guid mergeJobId, Guid operationId,
        Guid userId, string correlationId, CancellationToken cancellationToken);
}

public sealed class MoySkladCounterpartyGateway(
    HttpClient httpClient,
    IVendorTokenClient tokenClient,
    IMoySkladRateLimiter rateLimiter,
    ILogger<MoySkladCounterpartyGateway> logger) : IMoySkladCounterpartyGateway
{
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    public async Task<MoySkladRawResponse> GetAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, userId, cancellationToken);

        var filter = $"archived={archived.ToString().ToLowerInvariant()}";
        if (windowFrom is not null && windowTo is not null)
        {
            filter += $";updated>={FormatMoySkladTimestamp(windowFrom.Value)}" +
                      $";updated<{FormatMoySkladTimestamp(windowTo.Value)}";
        }

        var query = $"entity/counterparty?filter={Uri.EscapeDataString(filter)}" +
                    $"&limit={limit}&offset={offset}";
        if (windowFrom is not null)
            query += $"&order={Uri.EscapeDataString("updated;id")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Never log the Authorization header: it contains the account's access token.
        var requestUri = request.RequestUri is { IsAbsoluteUri: true } uri
            ? uri
            : new Uri(httpClient.BaseAddress!, request.RequestUri!);
        logger.LogInformation(
            "Sending MoySklad counterparties request: account_id={AccountId}, sync_run_id={SyncRunId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, archived={Archived}, limit={Limit}, offset={Offset}, method={Method}, url={Url}, accept={Accept}, accept_encoding={AcceptEncoding}",
            accountId,
            syncRunId,
            userId,
            correlationId,
            archived,
            limit,
            offset,
            request.Method,
            requestUri,
            request.Headers.Accept.ToString(),
            request.Headers.AcceptEncoding.ToString());

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new EgressException(
                503,
                "MOYSKLAD_UNAVAILABLE",
                "MoySklad is unavailable.",
                exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation(
                "MoySklad counterparties response received: account_id={AccountId}, sync_run_id={SyncRunId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, archived={Archived}, limit={Limit}, offset={Offset}, status={StatusCode}, content_type={ContentType}, headers={Headers}, duration_ms={DurationMs}",
                accountId,
                syncRunId,
                userId,
                correlationId,
                archived,
                limit,
                offset,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                SafeHeaders(response),
                stopwatch.Elapsed.TotalMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "MoySklad request failed: status={StatusCode}, message={ErrorMessage}, account_id={AccountId}, sync_run_id={SyncRunId}, archived={Archived}, limit={Limit}, offset={Offset}, correlation_id={CorrelationId}",
                    (int)response.StatusCode,
                    ExtractErrorMessage(json),
                    accountId,
                    syncRunId,
                    archived,
                    limit,
                    offset,
                    correlationId);
                ThrowForStatus(response.StatusCode);
            }
            if (string.IsNullOrWhiteSpace(json))
                throw new EgressException(502, "MOYSKLAD_INVALID_RESPONSE", "MoySklad returned an empty response.");

            return new MoySkladRawResponse(
                json,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                SafeHeaders(response));
        }
    }

    public Task<MoySkladRawResponse> UpdateAsync(
        Guid accountId,
        Guid counterpartyId,
        InternalCounterpartyUpdateRequest update,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["name"] = update.Name };
        if (update.Email is not null) payload["email"] = update.Email;
        if (update.Phone is not null) payload["phone"] = update.Phone;
        if (update.Description is not null) payload["description"] = update.Description;
        return PutAsync(accountId, counterpartyId, payload, mergeJobId, operationId, userId, correlationId, cancellationToken);
    }

    public Task<MoySkladRawResponse> ArchiveAsync(
        Guid accountId,
        Guid counterpartyId,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken) =>
        PutAsync(
            accountId, counterpartyId,
            new Dictionary<string, object?> { ["archived"] = true },
            mergeJobId, operationId, userId, correlationId, cancellationToken);

    private async Task<MoySkladRawResponse> PutAsync(
        Guid accountId,
        Guid counterpartyId,
        IReadOnlyDictionary<string, object?> payload,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, userId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"entity/counterparty/{counterpartyId:D}")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        logger.LogInformation(
            "Sending MoySklad counterparty mutation: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, counterparty_id={CounterpartyId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}",
            accountId, mergeJobId, operationId, counterpartyId, userId, correlationId);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation(
                "MoySklad counterparty mutation completed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, counterparty_id={CounterpartyId}, status={StatusCode}, correlation_id={CorrelationId}",
                accountId, mergeJobId, operationId, counterpartyId, (int)response.StatusCode, correlationId);
            if (!response.IsSuccessStatusCode)
                ThrowForMutationStatus(response.StatusCode);
            if (string.IsNullOrWhiteSpace(json))
                throw new EgressException(502, "MOYSKLAD_INVALID_RESPONSE", "MoySklad returned an empty response.");
            return new MoySkladRawResponse(
                json, (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType, SafeHeaders(response));
        }
    }

    private static string FormatMoySkladTimestamp(DateTimeOffset value) =>
        value.ToOffset(MoscowOffset).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "MoySklad returned an empty error response.";

        var trimmed = body.Trim();
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors
                    .EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object &&
                                   item.TryGetProperty("error", out var error) &&
                                   error.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("error").GetString())
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .Select(message => message!.Trim())
                    .ToArray();
                if (messages.Length > 0)
                    return Truncate(string.Join(" | ", messages));
            }
        }
        catch (JsonException)
        {
            // Fall back to the raw response below.
        }

        return Truncate(trimmed);
    }

    private static string Truncate(string value) =>
        value.Length <= 4096 ? value : value[..4096];

    private static void ThrowForStatus(HttpStatusCode statusCode)
    {
        if ((int)statusCode is >= 200 and <= 299)
            return;

        throw statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new EgressException(502, "MOYSKLAD_UNAUTHORIZED", "MoySklad rejected the access token."),
            HttpStatusCode.Forbidden =>
                new EgressException(502, "MOYSKLAD_FORBIDDEN", "MoySklad denied access."),
            HttpStatusCode.TooManyRequests =>
                new EgressException(429, "MOYSKLAD_RATE_LIMITED", "MoySklad rate limit exceeded."),
            _ when (int)statusCode >= 500 =>
                new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable."),
            _ => new EgressException(502, "MOYSKLAD_INVALID_RESPONSE", "MoySklad returned an unexpected status.")
        };
    }

    private static void ThrowForMutationStatus(HttpStatusCode statusCode)
    {
        throw statusCode switch
        {
            HttpStatusCode.BadRequest => new EgressException(400, "MOYSKLAD_VALIDATION_FAILED", "MoySklad rejected the counterparty update."),
            HttpStatusCode.Unauthorized => new EgressException(401, "MOYSKLAD_UNAUTHORIZED", "MoySklad rejected the access token."),
            HttpStatusCode.Forbidden => new EgressException(403, "MOYSKLAD_FORBIDDEN", "MoySklad denied access."),
            HttpStatusCode.NotFound => new EgressException(404, "MOYSKLAD_NOT_FOUND", "MoySklad counterparty was not found."),
            HttpStatusCode.TooManyRequests => new EgressException(429, "MOYSKLAD_RATE_LIMITED", "MoySklad rate limit exceeded."),
            _ when (int)statusCode >= 500 => new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable."),
            _ => new EgressException(502, "MOYSKLAD_INVALID_RESPONSE", "MoySklad returned an unexpected status.")
        };
    }

    private static IReadOnlyDictionary<string, string> SafeHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "X-RateLimit-Limit",
                     "X-RateLimit-Remaining",
                     "X-Lognex-Retry-After",
                     "X-Lognex-Reset"
                 })
        {
            if (response.Headers.TryGetValues(name, out var values))
                result[name] = values.FirstOrDefault() ?? string.Empty;
        }

        return result;
    }
}
