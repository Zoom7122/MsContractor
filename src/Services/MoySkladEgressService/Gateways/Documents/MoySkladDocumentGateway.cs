using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Clients;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;
using MsContractor.MoySkladEgressService.Validation;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Models.Exceptions;

namespace MsContractor.MoySkladEgressService.Gateways.Documents;

public interface IMoySkladDocumentGateway
{
        /// <summary>
    /// посредственно отправляет HTTP-запрос в МойСклад для получения документов на один тип документа
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="requestedByUserId"></param>
    /// <param name="correlationId"></param>
    /// <param name="documentType"> тип документа/тов который нужно получить</param>
    /// <param name="counterpartyIds"></param>
    /// <param name="limit"></param>
    /// <param name="offset"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
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

    Task<MoySkladDocumentChangeChunkResult> ChangeContractAgentsAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> contractIds,
        CancellationToken cancellationToken);

    Task<MoySkladDocumentChangeChunkResult> ChangeAgentAndContractAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
        CancellationToken cancellationToken);

}

public sealed class MoySkladDocumentGateway : IMoySkladDocumentGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly ILogger<MoySkladDocumentGateway> _logger;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly IMoySkladSingleDocumentResponseValidator _singleValidator;
    private readonly IMoySkladBulkDocumentResponseValidator _bulkValidator;
    private readonly MoySkladDocumentChangeRetryOptions _retryOptions;
    private readonly bool _isDevelopment;

    [ActivatorUtilitiesConstructor]
    public MoySkladDocumentGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        ILogger<MoySkladDocumentGateway> logger,
        IMoySkladResponseHandler responseHandler,
        IMoySkladSingleDocumentResponseValidator singleValidator,
        IMoySkladBulkDocumentResponseValidator bulkValidator,
        MoySkladDocumentChangeRetryOptions retryOptions,
        IHostEnvironment? environment)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _responseHandler = responseHandler;
        _singleValidator = singleValidator;
        _bulkValidator = bulkValidator;
        _retryOptions = retryOptions;
        _isDevelopment = environment?.IsDevelopment() == true;
    }

    public MoySkladDocumentGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        ILogger<MoySkladDocumentGateway> logger,
        MoySkladDocumentChangeRetryOptions? retryOptions = null)
        : this(
            httpClient,
            tokenClient,
            rateLimiter,
            logger,
            new MoySkladResponseHandler(
                new ForwardingLogger<MoySkladDocumentGateway, MoySkladResponseHandler>(logger)),
            new MoySkladSingleDocumentResponseValidator(),
            new MoySkladBulkDocumentResponseValidator(),
            retryOptions ?? MoySkladDocumentChangeRetryOptions.Disabled,
            null)
    {
    }

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

        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        var filter = string.Join(
            ';',
            counterpartyIds.Select(counterpartyId =>
                $"agent={new Uri(_httpClient.BaseAddress!, $"entity/counterparty/{counterpartyId:D}")}"));
        var requestUri = $"entity/{documentType}?filter={Uri.EscapeDataString(filter)}" +
                         $"&limit={limit}&offset={offset}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad document page request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, counterparties_count={CounterpartiesCount}, page_offset={Offset}, page_limit={Limit}",
            accountId,
            requestedByUserId,
            correlationId,
            documentType,
            counterpartyIds.Count,
            offset,
            limit);

        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, "GET", requestUri, documentType, null);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);

            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            _logger.LogInformation(
                "MoySklad document page response received: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, page_offset={Offset}, page_limit={Limit}, status={StatusCode}, duration_ms={DurationMs}",
                accountId,
                requestedByUserId,
                correlationId,
                documentType,
                offset,
                limit,
                responseBody.HttpStatus,
                stopwatch.Elapsed.TotalMilliseconds);

            DocumentCollectionDto? payload;
            try
            {
                payload = JsonSerializer.Deserialize<DocumentCollectionDto>(responseBody.Body, JsonOptions);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus,
                    $"MoySklad returned invalid document JSON: {exception.GetType().Name}.",
                    responseBody.Body, stopwatch.Elapsed);
            }

            if (payload?.Meta?.Size is null ||
                payload.Meta.Limit is null ||
                payload.Meta.Offset is null ||
                payload.Rows is null)
            {
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned incomplete document page metadata.",
                    responseBody.Body, stopwatch.Elapsed);
            }

            var rows = new List<MoySkladDocumentPageRow>(payload.Rows.Count);
            foreach (var row in payload.Rows)
            {
                if (row?.Agent?.Meta?.Href is null)
                    throw _responseHandler.ValidationFailure(
                        context, responseBody.HttpStatus, "MoySklad returned a document without agent metadata.",
                        responseBody.Body, stopwatch.Elapsed);

                rows.Add(new MoySkladDocumentPageRow(
                    row.Id,
                    row.Agent.Meta.Href,
                    row.Agent.Meta.Type,
                    IsCommissionReport(documentType) ? TryReadContractId(row.Contract) : null));
            }

            return new MoySkladDocumentPage(
                payload.Meta.Size.Value,
                payload.Meta.Limit.Value,
                payload.Meta.Offset.Value,
                rows,
                responseBody.HttpStatus);
        }
    }


    /// <summary>
    /// Валидирует и Меняет КА на документах которые обновляются PUT запросом
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="requestedByUserId"></param>
    /// <param name="mergeJobId"></param>
    /// <param name="operationId"></param>
    /// <param name="correlationId"></param>
    /// <param name="mainCounterpartyId"></param>
    /// <param name="documentType"></param>
    /// <param name="documents"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
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

        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        var agent = new
        {
            meta = new
            {
                href = new Uri(_httpClient.BaseAddress!, $"entity/counterparty/{mainCounterpartyId:D}").ToString(),
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
                    href = new Uri(_httpClient.BaseAddress!, $"entity/{documentType}/{item.DocumentId:D}").ToString(),
                    type = documentType,
                    mediaType = "application/json"
                },
                agent
            }).ToArray();

        var method = isSingle ? HttpMethod.Put : HttpMethod.Post;
        var endpoint = isSingle
            ? $"entity/{documentType}/{documents[0].DocumentId:D}"
            : $"entity/{documentType}/batch";
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var retryAttempt = 0;
        EgressException? previousException = null;

        while (true)
        {
            if (retryAttempt > 0)
            {
                var delay = GetRetryDelay(retryAttempt);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);

                _logger.LogWarning(
                    "Retrying MoySklad document counterparty mutation: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, chunk_size={ChunkSize}, retry_attempt={RetryAttempt}, max_retries={MaxRetries}, retry_delay_ms={RetryDelayMs}, previous_http_status={HttpStatus}, previous_error_code={ErrorCode}",
                    accountId, mergeJobId, operationId, correlationId, documentType, method.Method,
                    documents.Count, retryAttempt, _retryOptions.MaxRetries, delay.TotalMilliseconds,
                    previousException?.HttpStatus, previousException?.Code);

                if (_retryOptions.LogPayloadOnRetry)
                {
                    _logger.LogInformation(
                        "MoySklad document counterparty retry payload: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, retry_attempt={RetryAttempt}, request_body={RequestBody}",
                        accountId, mergeJobId, operationId, correlationId, documentType, retryAttempt, payloadJson);
                }
            }

            try
            {
                return await SendCounterpartyMutationAttemptAsync(
                    accountId, requestedByUserId, mergeJobId, operationId, correlationId, mainCounterpartyId,
                    documentType, documents, isSingle, method, endpoint, payloadJson, accessToken,
                    retryAttempt, cancellationToken);
            }
            catch (EgressException exception) when (retryAttempt < _retryOptions.MaxRetries)
            {
                previousException = exception;
                retryAttempt++;
            }
        }
    }

    private async Task<MoySkladDocumentChangeChunkResult> SendCounterpartyMutationAttemptAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
        bool isSingle,
        HttpMethod method,
        string endpoint,
        string payloadJson,
        string accessToken,
        int retryAttempt,
        CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad document counterparty mutation: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, chunk_size={ChunkSize}, retry_attempt={RetryAttempt}",
            accountId, mergeJobId, operationId, requestedByUserId, correlationId, documentType,
            request.Method.Method, documents.Count, retryAttempt);

        var context = new MoySkladRequestContext(
            accountId,
            correlationId,
            mergeJobId,
            operationId,
            request.Method.Method,
            request.RequestUri!.ToString(),
            documentType,
            isSingle ? documents[0].DocumentId : null,
            retryAttempt);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad document update timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);

            if (!isSingle)
            {
                var batchResponseJson = await ReadMutationResponseJsonAsync(
                    response, context, stopwatch, cancellationToken);

                if (TryReadBatchItemErrors(
                        batchResponseJson,
                        documentType,
                        documents,
                        _httpClient.BaseAddress!,
                        out var skippedDocuments))
                {
                    var skippedIds = skippedDocuments.Select(item => item.DocumentId).ToArray();
                    var skippedIdSet = skippedIds.ToHashSet();
                    var changedDocuments = documents.Where(item => !skippedIdSet.Contains(item.DocumentId)).ToArray();

                    _logger.LogWarning(
                        "MoySklad document counterparty batch returned item errors: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, http_status={HttpStatus}, changed_count={ChangedCount}, skipped_count={SkippedCount}, skipped_document_ids={SkippedDocumentIds}",
                        accountId, mergeJobId, operationId, correlationId, documentType, method.Method,
                        (int)response.StatusCode, changedDocuments.Length, skippedDocuments.Count, skippedIds);

                    return new MoySkladDocumentChangeChunkResult(changedDocuments, [])
                    {
                        SkippedDocuments = skippedDocuments
                    };
                }
            }

            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            _logger.LogInformation(
                "MoySklad document counterparty mutation completed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, chunk_size={ChunkSize}, retry_attempt={RetryAttempt}, status={StatusCode}, duration_ms={DurationMs}",
                accountId, mergeJobId, operationId, correlationId, documentType, request.Method.Method,
                documents.Count, retryAttempt, responseBody.HttpStatus, stopwatch.Elapsed.TotalMilliseconds);
            if (string.IsNullOrWhiteSpace(json))
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned an empty document update response.", json,
                    stopwatch.Elapsed);

            JsonDocument responseDocument;
            try
            {
                responseDocument = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw _responseHandler.ValidationFailure(
                    context,
                    responseBody.HttpStatus,
                    $"MoySklad returned invalid document update JSON: {exception.GetType().Name}.",
                    json,
                    stopwatch.Elapsed);
            }

            using (responseDocument)
            {
                if (isSingle)
                {
                    var validationError = _singleValidator.Validate(
                        responseDocument.RootElement, documents[0], mainCounterpartyId);
                    if (validationError is not null)
                        throw _responseHandler.ValidationFailure(
                            context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                    return new MoySkladDocumentChangeChunkResult([documents[0]], []);
                }

                var validation = _bulkValidator.Validate(
                    responseDocument.RootElement, documents, mainCounterpartyId);
                var failures = validation.Failed.Select(item => new MoySkladDocumentChangeFailure(
                    item.Document.DocumentType,
                    item.Document.DocumentId,
                    item.Code,
                    item.Message,
                    responseBody.HttpStatus,
                    false,
                    request.RequestUri!.ToString(),
                    ValidationError: item.Message)).ToArray();
                if (failures.Length > 0)
                {
                    var validationError = string.Join(" | ", failures.Select(item => item.Message));
                    _ = _responseHandler.ValidationFailure(
                        context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                }
                return new MoySkladDocumentChangeChunkResult(validation.Succeeded, failures);
            }
        }
    }

    private async Task<string> ReadMutationResponseJsonAsync(
        HttpResponseMessage response,
        MoySkladRequestContext context,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad response timed out.", stopwatch.Elapsed, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }
    }

    private static bool TryReadBatchItemErrors(
        string responseJson,
        string documentType,
        IReadOnlyList<MoySkladDocumentChangeItem> requestedDocuments,
        Uri moySkladBaseAddress,
        out IReadOnlyList<MoySkladDocumentChangeSkippedItem> skippedDocuments)
    {
        skippedDocuments = [];
        if (string.IsNullOrWhiteSpace(responseJson))
            return false;

        try
        {
            using var response = JsonDocument.Parse(responseJson);
            var root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != requestedDocuments.Count)
                return false;

            var requestedIds = requestedDocuments.Select(item => item.DocumentId).ToHashSet();
            var errorsByDocumentId = new Dictionary<Guid, string>();
            foreach (var responseItem in root.EnumerateArray())
            {
                if (responseItem.ValueKind != JsonValueKind.Object ||
                    !responseItem.TryGetProperty("errors", out var errors))
                    continue;
                if (errors.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var error in errors.EnumerateArray())
                {
                    if (!TryReadBatchErrorDocumentId(
                            error, documentType, moySkladBaseAddress, out var documentId) ||
                        !requestedIds.Contains(documentId))
                        return false;

                    errorsByDocumentId.TryAdd(documentId, BuildBatchErrorReason(error));
                }
            }

            if (errorsByDocumentId.Count == 0)
                return false;

            skippedDocuments = requestedDocuments
                .Where(item => errorsByDocumentId.ContainsKey(item.DocumentId))
                .Select(item => new MoySkladDocumentChangeSkippedItem(
                    item.DocumentType,
                    item.DocumentId,
                    errorsByDocumentId[item.DocumentId]))
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadBatchErrorDocumentId(
        JsonElement error,
        string documentType,
        Uri moySkladBaseAddress,
        out Guid documentId)
    {
        documentId = Guid.Empty;
        if (error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var hrefElement) || hrefElement.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(hrefElement.GetString(), UriKind.Absolute, out var href) ||
            !string.Equals(href.Scheme, moySkladBaseAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(href.Authority, moySkladBaseAddress.Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        var pathSegments = href.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return pathSegments.Length >= 3 &&
               string.Equals(pathSegments[^3], "entity", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(pathSegments[^2], documentType, StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(pathSegments[^1], out documentId) &&
               documentId != Guid.Empty;
    }

    private static string BuildBatchErrorReason(JsonElement error)
    {
        var code = ReadErrorScalar(error, "code");
        var message = ReadErrorScalar(error, "error");
        var safeCode = string.IsNullOrWhiteSpace(code)
            ? null
            : MoySkladResponseHandler.SanitizeAndTruncate(code, 64);
        var reason = string.IsNullOrWhiteSpace(safeCode)
            ? "MOYSKLAD_BATCH_ITEM_ERROR"
            : $"MOYSKLAD_{safeCode}";

        if (string.IsNullOrWhiteSpace(message))
            return reason;

        var safeMessage = MoySkladResponseHandler.SanitizeAndTruncate(message, 512);
        return string.IsNullOrWhiteSpace(safeMessage) ? reason : $"{reason}: {safeMessage}";
    }

    private static string? ReadErrorScalar(JsonElement error, string propertyName)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private TimeSpan GetRetryDelay(int retryAttempt)
    {
        var milliseconds = _retryOptions.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, retryAttempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue));
    }

    public Task<MoySkladDocumentChangeChunkResult> ChangeContractAgentsAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> contractIds,
        CancellationToken cancellationToken)
    {
        if (contractIds.Count is < 1 or > 1000 || contractIds.Any(id => id == Guid.Empty) ||
            contractIds.Distinct().Count() != contractIds.Count)
        {
            throw new ArgumentException("Contract mutation chunk is invalid.", nameof(contractIds));
        }

        return ChangeAgentAsync(
            accountId, requestedByUserId, mergeJobId, operationId, correlationId, mainCounterpartyId,
            "contract", contractIds.Select(id => new MoySkladDocumentChangeItem("contract", id)).ToArray(),
            cancellationToken);
    }

    public async Task<MoySkladDocumentChangeChunkResult> ChangeAgentAndContractAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
        CancellationToken cancellationToken)
    {
        if (!SupportedMoySkladDocumentTypes.Contains(documentType) || documents.Count is < 1 or > 1000 ||
            documents.Any(item => !string.Equals(item.DocumentType, documentType, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Document mutation chunk is invalid.", nameof(documents));
        }

        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);
        var agent = EntityReference("counterparty", mainCounterpartyId);
        var isSingle = documents.Count == 1;
        object payload = isSingle
            ? DocumentPayload(documents[0], agent)
            : documents.Select(item => DocumentBatchPayload(item, documentType, agent)).ToArray();
        var requestUri = isSingle
            ? $"entity/{documentType}/{documents[0].DocumentId:D}"
            : $"entity/{documentType}/batch";
        using var request = new HttpRequestMessage(isSingle ? HttpMethod.Put : HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var context = new MoySkladRequestContext(
            accountId, correlationId, mergeJobId, operationId, request.Method.Method, request.RequestUri!.ToString(),
            documentType, isSingle ? documents[0].DocumentId : null);
        if (_isDevelopment && IsCommissionReport(documentType))
        {
            var endpoint = new Uri(_httpClient.BaseAddress!, request.RequestUri!).ToString();
            var requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation(
                "Sending MoySklad commission report agent and contract request: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, endpoint={Endpoint}, chunk_size={ChunkSize}, request_body={RequestBody}",
                accountId, mergeJobId, operationId, requestedByUserId, correlationId, documentType,
                request.Method.Method, endpoint, documents.Count, requestBody);
        }
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(context, "MoySklad document update timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);
            var plainDocuments = documents
                .Select(item => new MoySkladDocumentChangeItem(item.DocumentType, item.DocumentId)).ToArray();
            IReadOnlyList<MoySkladDocumentChangeSkippedItem> skippedDocuments = [];
            MoySkladResponseBody? responseBody = null;
            if (!isSingle && (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.PreconditionFailed))
            {
                var batchResponseJson = await ReadMutationResponseJsonAsync(
                    response, context, stopwatch, cancellationToken);
                if (TryReadBatchItemErrors(
                        batchResponseJson, documentType, plainDocuments, _httpClient.BaseAddress!,
                        out skippedDocuments))
                {
                    responseBody = new MoySkladResponseBody(batchResponseJson, (int)response.StatusCode);
                    _logger.LogWarning(
                        "MoySklad document agent and contract batch returned item errors: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, http_status={HttpStatus}, skipped_count={SkippedCount}, skipped_document_ids={SkippedDocumentIds}",
                        accountId, mergeJobId, operationId, correlationId, documentType, request.Method.Method,
                        (int)response.StatusCode, skippedDocuments.Count,
                        skippedDocuments.Select(item => item.DocumentId).ToArray());
                }
            }

            responseBody ??= await _responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            if (string.IsNullOrWhiteSpace(json))
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned an empty document update response.", json,
                    stopwatch.Elapsed);

            JsonDocument responseDocument;
            try { responseDocument = JsonDocument.Parse(json); }
            catch (JsonException exception)
            {
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus,
                    $"MoySklad returned invalid document update JSON: {exception.GetType().Name}.", json,
                    stopwatch.Elapsed);
            }

            using (responseDocument)
            {
                if (isSingle)
                {
                    var validationError = _singleValidator.Validate(
                        responseDocument.RootElement, plainDocuments[0], mainCounterpartyId)
                        ?? ValidateContract(responseDocument.RootElement, documents[0].Contract);
                    if (validationError is not null)
                        throw _responseHandler.ValidationFailure(
                            context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                    return new MoySkladDocumentChangeChunkResult(plainDocuments, []);
                }

                var skippedIds = skippedDocuments.Select(item => item.DocumentId).ToHashSet();
                var expectedDocuments = plainDocuments.Where(item => !skippedIds.Contains(item.DocumentId)).ToArray();
                var validation = _bulkValidator.Validate(responseDocument.RootElement, plainDocuments, mainCounterpartyId);
                var validationFailures = validation.Failed.Where(item => !skippedIds.Contains(item.Document.DocumentId)).ToArray();
                var failedIds = validationFailures.Select(item => item.Document.DocumentId).ToHashSet();
                var failures = validationFailures.Select(item => new MoySkladDocumentChangeFailure(
                    item.Document.DocumentType, item.Document.DocumentId, item.Code, item.Message,
                    responseBody.HttpStatus, false, request.RequestUri!.ToString(), ValidationError: item.Message)).ToList();
                var responseById = responseDocument.RootElement.ValueKind == JsonValueKind.Array
                    ? responseDocument.RootElement.EnumerateArray()
                        .Where(item => MoySkladDocumentJson.TryReadId(item, out _))
                        .GroupBy(item => { MoySkladDocumentJson.TryReadId(item, out var id); return id; })
                        .ToDictionary(group => group.Key, group => group.First())
                    : new Dictionary<Guid, JsonElement>();
                foreach (var document in documents.Where(item =>
                             !skippedIds.Contains(item.DocumentId) && !failedIds.Contains(item.DocumentId)))
                {
                    if (!responseById.TryGetValue(document.DocumentId, out var item))
                        continue;
                    var contractError = ValidateContract(item, document.Contract);
                    if (contractError is null)
                        continue;
                    failedIds.Add(document.DocumentId);
                    failures.Add(new MoySkladDocumentChangeFailure(
                        document.DocumentType, document.DocumentId, "MOYSKLAD_RESPONSE_VALIDATION_FAILED", contractError,
                        responseBody.HttpStatus, false, request.RequestUri!.ToString(), ValidationError: contractError));
                }
                if (failures.Count > 0)
                    _ = _responseHandler.ValidationFailure(
                        context, responseBody.HttpStatus, string.Join(" | ", failures.Select(item => item.Message)), json,
                        stopwatch.Elapsed);
                return new MoySkladDocumentChangeChunkResult(
                    expectedDocuments.Where(item => !failedIds.Contains(item.DocumentId)).ToArray(), failures)
                {
                    SkippedDocuments = skippedDocuments
                };
            }
        }
    }

    private Task<MoySkladDocumentChangeChunkResult> ChangeAgentAsync(
        Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
        Guid mainCounterpartyId, string entityType, IReadOnlyList<MoySkladDocumentChangeItem> entities,
        CancellationToken cancellationToken)
    {
        // Contracts use the same response invariants as documents: returned id and target agent must match.
        return ChangeCounterpartyAsyncCore(
            accountId, requestedByUserId, mergeJobId, operationId, correlationId, mainCounterpartyId,
            entityType, entities, cancellationToken);
    }

    private async Task<MoySkladDocumentChangeChunkResult> ChangeCounterpartyAsyncCore(
        Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
        Guid mainCounterpartyId, string entityType, IReadOnlyList<MoySkladDocumentChangeItem> entities,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);
        var agent = EntityReference("counterparty", mainCounterpartyId);
        var isSingle = entities.Count == 1;
        object payload = isSingle ? new { agent } : entities.Select(item => new
        {
            meta = EntityReference(entityType, item.DocumentId).meta,
            agent
        }).ToArray();
        var requestUri = isSingle ? $"entity/{entityType}/{entities[0].DocumentId:D}" : $"entity/{entityType}/batch";
        using var request = new HttpRequestMessage(isSingle ? HttpMethod.Put : HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var context = new MoySkladRequestContext(accountId, correlationId, mergeJobId, operationId,
            request.Method.Method, request.RequestUri!.ToString(), entityType, isSingle ? entities[0].DocumentId : null);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw _responseHandler.TransportFailure(context, "MoySklad document update timed out.", stopwatch.Elapsed, exception); }
        catch (HttpRequestException exception)
        { throw _responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception); }
        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            if (string.IsNullOrWhiteSpace(json))
                throw _responseHandler.ValidationFailure(context, responseBody.HttpStatus,
                    "MoySklad returned an empty document update response.", json, stopwatch.Elapsed);
            JsonDocument responseDocument;
            try { responseDocument = JsonDocument.Parse(json); }
            catch (JsonException exception)
            {
                throw _responseHandler.ValidationFailure(context, responseBody.HttpStatus,
                $"MoySklad returned invalid document update JSON: {exception.GetType().Name}.", json, stopwatch.Elapsed);
            }
            using (responseDocument)
            {
                if (isSingle)
                {
                    var validationError = _singleValidator.Validate(responseDocument.RootElement, entities[0], mainCounterpartyId);
                    if (validationError is not null)
                        throw _responseHandler.ValidationFailure(context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                    return new MoySkladDocumentChangeChunkResult([entities[0]], []);
                }
                var validation = _bulkValidator.Validate(responseDocument.RootElement, entities, mainCounterpartyId);
                var failures = validation.Failed.Select(item => new MoySkladDocumentChangeFailure(
                    item.Document.DocumentType, item.Document.DocumentId, item.Code, item.Message, responseBody.HttpStatus,
                    false, request.RequestUri!.ToString(), ValidationError: item.Message)).ToArray();
                if (failures.Length > 0)
                    _ = _responseHandler.ValidationFailure(context, responseBody.HttpStatus,
                        string.Join(" | ", failures.Select(item => item.Message)), json, stopwatch.Elapsed);
                return new MoySkladDocumentChangeChunkResult(validation.Succeeded, failures);
            }
        }
    }

    private EntityReferencePayload EntityReference(string entityType, Guid id) => new(new EntityMeta(
        new Uri(_httpClient.BaseAddress!, $"entity/{entityType}/{id:D}").ToString(),
        entityType,
        "application/json"));

    private object DocumentPayload(MoySkladDocumentChangeAgentAndContractItem document, object agent) =>
        document.Contract is { } contract
            ? new { agent, contract = EntityReference("contract", contract) }
            : new { agent };

    private object DocumentBatchPayload(MoySkladDocumentChangeAgentAndContractItem document, string documentType, object agent) =>
        document.Contract is { } contract
            ? new { meta = EntityReference(documentType, document.DocumentId).meta, agent, contract = EntityReference("contract", contract) }
            : new { meta = EntityReference(documentType, document.DocumentId).meta, agent };

    private static string? ValidateContract(JsonElement document, Guid? expectedContract) =>
        expectedContract is null
            ? null
            : !MoySkladDocumentJson.TryReadEntityId(document, "contract", out var actualContract) || actualContract != expectedContract
                ? "MoySklad returned a document with an unexpected contract."
                : null;

    private sealed record EntityReferencePayload(EntityMeta meta);
    private sealed record EntityMeta(string href, string type, string mediaType);

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
        public JsonElement? Contract { get; init; }
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

    private static bool IsCommissionReport(string documentType) =>
        string.Equals(documentType, "commissionreportin", StringComparison.Ordinal) ||
        string.Equals(documentType, "commissionreportout", StringComparison.Ordinal);

    private static Guid? TryReadContractId(JsonElement? contract)
    {
        if (contract is not { ValueKind: JsonValueKind.Object } value)
            return null;

        if (value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            Guid.TryParse(id.GetString(), out var parsedId) && parsedId != Guid.Empty)
        {
            return parsedId;
        }

        if (!value.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return Guid.TryParse(segment, out parsedId) && parsedId != Guid.Empty ? parsedId : null;
    }
}
