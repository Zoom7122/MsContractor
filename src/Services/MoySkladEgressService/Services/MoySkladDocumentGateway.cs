using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MsContractor.Contracts.Internal;

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

    Task<MoySkladDocumentChangeChunkResult> ChangeCounterpartyAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
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

    public async Task<MoySkladDocumentChangeChunkResult> ChangeCounterpartyAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
        CancellationToken cancellationToken)
    {
        if (!SupportedMoySkladDocumentTypes.Contains(documentType) || documents.Count is < 1 or > 1000 ||
            documents.Any(item => !string.Equals(item.DocumentType, documentType, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Document mutation chunk is invalid.", nameof(documents));
        }

        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, requestedByUserId, cancellationToken);

        var agent = new
        {
            meta = new
            {
                href = new Uri(httpClient.BaseAddress!, $"entity/counterparty/{mainCounterpartyId:D}").ToString(),
                type = "counterparty",
                mediaType = "application/json"
            }
        };
        var isSingle = documents.Count == 1;
        object payload = isSingle
            ? new { agent }
            : documents.Select(item => new
            {
                meta = new
                {
                    href = new Uri(httpClient.BaseAddress!, $"entity/{documentType}/{item.DocumentId:D}").ToString(),
                    type = documentType,
                    mediaType = "application/json"
                },
                agent
            }).ToArray();
        using var request = new HttpRequestMessage(
            isSingle ? HttpMethod.Put : HttpMethod.Post,
            isSingle
                ? $"entity/{documentType}/{documents[0].DocumentId:D}"
                : $"entity/{documentType}/batch")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        logger.LogInformation(
            "Sending MoySklad document counterparty mutation: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, chunk_size={ChunkSize}",
            accountId, mergeJobId, operationId, requestedByUserId, correlationId, documentType,
            request.Method.Method, documents.Count);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad document update timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad document update timed out.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                throw new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", exception);
            }

            logger.LogInformation(
                "MoySklad document counterparty mutation completed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, chunk_size={ChunkSize}, status={StatusCode}",
                accountId, mergeJobId, operationId, correlationId, documentType, request.Method.Method,
                documents.Count, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                var safeError = ExtractSafeError(json);
                logger.LogWarning(
                    "MoySklad document counterparty mutation rejected: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, status={StatusCode}, error_code={ErrorCode}, error_message={ErrorMessage}",
                    accountId, mergeJobId, operationId, correlationId, documentType, (int)response.StatusCode,
                    MutationErrorCode(response.StatusCode), safeError);
                throw MutationException(response.StatusCode, safeError);
            }
            if (string.IsNullOrWhiteSpace(json))
                throw InvalidMutationResponse("MoySklad returned an empty document update response.");

            JsonDocument responseDocument;
            try
            {
                responseDocument = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw InvalidMutationResponse("MoySklad returned invalid document update JSON.", exception);
            }

            using (responseDocument)
            {
                var items = isSingle
                    ? new[] { responseDocument.RootElement }
                    : ReadBatchItems(responseDocument.RootElement, documents.Count);
                var changed = new List<MoySkladDocumentChangeItem>(documents.Count);
                var failures = new List<MoySkladDocumentChangeFailure>();
                for (var index = 0; index < documents.Count; index++)
                {
                    var expected = documents[index];
                    if (TryReadItemFailure(items[index], expected, out var failure))
                    {
                        failures.Add(failure!);
                        continue;
                    }

                    ValidateChangedDocument(items[index], expected, mainCounterpartyId);
                    changed.Add(expected);
                }
                return new MoySkladDocumentChangeChunkResult(changed, failures);
            }
        }
    }

    private static JsonElement[] ReadBatchItems(JsonElement root, int expectedCount)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw InvalidMutationResponse("MoySklad returned a non-array batch document update response.");
        var items = root.EnumerateArray().ToArray();
        if (items.Length != expectedCount)
            throw InvalidMutationResponse("MoySklad returned an incomplete batch document update response.");
        return items;
    }

    private static bool TryReadItemFailure(
        JsonElement item,
        MoySkladDocumentChangeItem expected,
        out MoySkladDocumentChangeFailure? failure)
    {
        failure = null;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return false;

        var first = errors.EnumerateArray().FirstOrDefault();
        var rawCode = first.ValueKind == JsonValueKind.Object ? Property(first, "code") : null;
        var message = first.ValueKind == JsonValueKind.Object
            ? string.Join(", ", new[] { Property(first, "error"), Property(first, "error_message"), Property(first, "parameter") }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Empty;
        failure = new MoySkladDocumentChangeFailure(
            expected.DocumentType,
            expected.DocumentId,
            rawCode is null ? "MOYSKLAD_DOCUMENT_CHANGE_REJECTED" : $"MOYSKLAD_{rawCode}",
            string.IsNullOrWhiteSpace(message) ? "MoySklad rejected the document update." : Truncate(message),
            400,
            false);
        return true;
    }

    private static void ValidateChangedDocument(
        JsonElement item,
        MoySkladDocumentChangeItem expected,
        Guid mainCounterpartyId)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(idElement.GetString(), out var id) || id != expected.DocumentId ||
            !item.TryGetProperty("meta", out var meta) ||
            !string.Equals(Property(meta, "type"), expected.DocumentType, StringComparison.Ordinal) ||
            !item.TryGetProperty("agent", out var agent) ||
            !agent.TryGetProperty("meta", out var agentMeta) ||
            !string.Equals(Property(agentMeta, "type"), "counterparty", StringComparison.Ordinal) ||
            !TryParseEntityId(Property(agentMeta, "href"), "counterparty", out var agentId) ||
            agentId != mainCounterpartyId)
        {
            throw InvalidMutationResponse("MoySklad returned an inconsistent document update response.");
        }
    }

    private static bool TryParseEntityId(string? href, string entityType, out Guid id)
    {
        id = Guid.Empty;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 &&
               string.Equals(segments[^3], "entity", StringComparison.Ordinal) &&
               string.Equals(segments[^2], entityType, StringComparison.Ordinal) &&
               Guid.TryParse(segments[^1], out id) && id != Guid.Empty;
    }

    private static EgressException InvalidMutationResponse(string message, Exception? inner = null) =>
        new(502, "MOYSKLAD_DOCUMENT_CHANGE_INVALID_RESPONSE", message, inner);

    private static EgressException MutationException(HttpStatusCode statusCode, string safeError) => statusCode switch
    {
        HttpStatusCode.BadRequest => new EgressException(400, "MOYSKLAD_DOCUMENT_CHANGE_REJECTED", safeError),
        HttpStatusCode.Unauthorized => new EgressException(401, "MOYSKLAD_UNAUTHORIZED", safeError),
        HttpStatusCode.Forbidden => new EgressException(403, "MOYSKLAD_FORBIDDEN", safeError),
        HttpStatusCode.NotFound => new EgressException(404, "MOYSKLAD_NOT_FOUND", safeError),
        HttpStatusCode.TooManyRequests => new EgressException(429, "MOYSKLAD_RATE_LIMITED", safeError),
        _ when (int)statusCode >= 500 => new EgressException(503, "MOYSKLAD_UNAVAILABLE", safeError),
        _ => new EgressException(502, "MOYSKLAD_DOCUMENT_CHANGE_INVALID_RESPONSE", safeError)
    };

    private static string MutationErrorCode(HttpStatusCode statusCode) => MutationException(statusCode, string.Empty).Code;

    private static string ExtractSafeError(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "MoySklad returned an empty error response.";
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var values = errors.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(item => string.Join(", ", new[]
                    {
                        Property(item, "code") is { } code ? $"code={code}" : null,
                        Property(item, "error") is { } error ? $"error={error}" : null,
                        Property(item, "error_message") is { } details ? $"error_message={details}" : null,
                        Property(item, "parameter") is { } parameter ? $"parameter={parameter}" : null
                    }.Where(value => value is not null)))
                    .Where(value => value.Length > 0)
                    .ToArray();
                if (values.Length > 0) return Truncate(string.Join(" | ", values));
            }
        }
        catch (JsonException)
        {
            // Never expose an unstructured upstream response.
        }
        return "MoySklad returned an unstructured error response.";
    }

    private static string Truncate(string value) => value.Length <= 4096 ? value : value[..4096];

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
