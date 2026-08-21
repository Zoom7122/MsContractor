using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MsContractor.MoySkladEgressService.Services;

public interface IMoySkladDocumentGateway
{
    Task<MoySkladDocumentPage> GetPageAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        string documentType,
        IReadOnlyList<Guid> counterpartyIds,
        int limit,
        int offset,
        CancellationToken cancellationToken);
}

public sealed class MoySkladDocumentGateway(
    HttpClient httpClient,
    IVendorTokenClient tokenClient,
    IMoySkladRateLimiter rateLimiter,
    ILogger<MoySkladDocumentGateway> logger) : IMoySkladDocumentGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MoySkladDocumentPage> GetPageAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        string documentType,
        IReadOnlyList<Guid> counterpartyIds,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        if (!SupportedMoySkladDocumentTypes.Contains(documentType))
            throw new ArgumentOutOfRangeException(nameof(documentType));

        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, requestedByUserId, cancellationToken);

        var filter = string.Join(
            ';',
            counterpartyIds.Select(counterpartyId =>
                $"agent={new Uri(httpClient.BaseAddress!, $"entity/counterparty/{counterpartyId:D}")}"));
        var requestUri = $"entity/{documentType}?filter={Uri.EscapeDataString(filter)}" +
                         $"&limit={limit}&offset={offset}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        logger.LogInformation(
            "Sending MoySklad document page request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, counterparties_count={CounterpartiesCount}, page_offset={Offset}, page_limit={Limit}",
            accountId,
            requestedByUserId,
            correlationId,
            documentType,
            counterpartyIds.Count,
            offset,
            limit);

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
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            logger.LogInformation(
                "MoySklad document page response received: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, page_offset={Offset}, page_limit={Limit}, status={StatusCode}, duration_ms={DurationMs}",
                accountId,
                requestedByUserId,
                correlationId,
                documentType,
                offset,
                limit,
                (int)response.StatusCode,
                stopwatch.Elapsed.TotalMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                var safeError = await ReadSafeErrorAsync(response, cancellationToken);
                logger.LogWarning(
                    "MoySklad document page request failed: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, page_offset={Offset}, page_limit={Limit}, status={StatusCode}, error={ErrorMessage}",
                    accountId,
                    requestedByUserId,
                    correlationId,
                    documentType,
                    offset,
                    limit,
                    (int)response.StatusCode,
                    safeError);
                ThrowForStatus(response.StatusCode, safeError);
            }

            DocumentCollectionDto? payload;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                payload = await JsonSerializer.DeserializeAsync<DocumentCollectionDto>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw InvalidResponse("MoySklad returned invalid document JSON.", exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad request timed out.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new EgressException(
                    503,
                    "MOYSKLAD_UNAVAILABLE",
                    "MoySklad is unavailable.",
                    exception);
            }

            if (payload?.Meta?.Size is null ||
                payload.Meta.Limit is null ||
                payload.Meta.Offset is null ||
                payload.Rows is null)
            {
                throw InvalidResponse("MoySklad returned incomplete document page metadata.");
            }

            var rows = new List<MoySkladDocumentPageRow>(payload.Rows.Count);
            foreach (var row in payload.Rows)
            {
                if (row?.Agent?.Meta?.Href is null)
                    throw InvalidResponse("MoySklad returned a document without agent metadata.");

                rows.Add(new MoySkladDocumentPageRow(
                    row.Id,
                    row.Agent.Meta.Href,
                    row.Agent.Meta.Type));
            }

            return new MoySkladDocumentPage(
                payload.Meta.Size.Value,
                payload.Meta.Limit.Value,
                payload.Meta.Offset.Value,
                rows,
                (int)response.StatusCode);
        }
    }

    private static EgressException InvalidResponse(string message, Exception? inner = null) =>
        new(502, "MOYSKLAD_DOCUMENT_DISCOVERY_INVALID_RESPONSE", message, inner);

    private static void ThrowForStatus(HttpStatusCode statusCode, string safeError)
    {
        var details = $"MoySklad error: {safeError}";
        throw statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new EgressException(502, "MOYSKLAD_UNAUTHORIZED", $"MoySklad rejected the access token. {details}"),
            HttpStatusCode.Forbidden =>
                new EgressException(502, "MOYSKLAD_FORBIDDEN", $"MoySklad denied access. {details}"),
            HttpStatusCode.TooManyRequests =>
                new EgressException(429, "MOYSKLAD_RATE_LIMITED", $"MoySklad rate limit exceeded. {details}"),
            _ when (int)statusCode >= 500 =>
                new EgressException(503, "MOYSKLAD_UNAVAILABLE", $"MoySklad is unavailable. {details}"),
            _ => new EgressException(
                502,
                "MOYSKLAD_DOCUMENT_DISCOVERY_INVALID_RESPONSE",
                $"MoySklad returned an unexpected document response status. {details}")
        };
    }

    private static async Task<string> ReadSafeErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array)
            {
                return "Unstructured MoySklad error response.";
            }

            var safeParts = errors.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new
                {
                    Code = Property(item, "code"),
                    Error = Property(item, "error"),
                    Parameter = Property(item, "parameter")
                })
                .Select(item => string.Join(
                    ", ",
                    new[]
                    {
                        item.Code is null ? null : $"code={item.Code}",
                        item.Error is null ? null : $"error={item.Error}",
                        item.Parameter is null ? null : $"parameter={item.Parameter}"
                    }.Where(value => value is not null)))
                .Where(value => value.Length > 0)
                .ToArray();
            return safeParts.Length == 0
                ? "Unstructured MoySklad error response."
                : string.Join(" | ", safeParts);
        }
        catch (Exception exception) when (
            exception is JsonException or HttpRequestException or IOException or NotSupportedException)
        {
            return "Unstructured MoySklad error response.";
        }
    }

    private static string? Property(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private sealed class DocumentCollectionDto
    {
        public DocumentCollectionMetaDto? Meta { get; init; }
        public List<DocumentRowDto?>? Rows { get; init; }
    }

    private sealed class DocumentCollectionMetaDto
    {
        public int? Size { get; init; }
        public int? Limit { get; init; }
        public int? Offset { get; init; }
    }

    private sealed class DocumentRowDto
    {
        public Guid Id { get; init; }
        public DocumentAgentDto? Agent { get; init; }
    }

    private sealed class DocumentAgentDto
    {
        public DocumentAgentMetaDto? Meta { get; init; }
    }

    private sealed class DocumentAgentMetaDto
    {
        public string? Href { get; init; }
        public string? Type { get; init; }
    }
}
