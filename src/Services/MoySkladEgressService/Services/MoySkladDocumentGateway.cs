using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MsContractor.Contracts.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly HttpClient httpClient;
    private readonly IVendorTokenClient tokenClient;
    private readonly IMoySkladRateLimiter rateLimiter;
    private readonly ILogger<MoySkladDocumentGateway> logger;
    private readonly IMoySkladResponseHandler responseHandler;
    private readonly IMoySkladSingleDocumentResponseValidator singleValidator;
    private readonly IMoySkladBulkDocumentResponseValidator bulkValidator;

    [ActivatorUtilitiesConstructor]
    public MoySkladDocumentGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        ILogger<MoySkladDocumentGateway> logger,
        IMoySkladResponseHandler responseHandler,
        IMoySkladSingleDocumentResponseValidator singleValidator,
        IMoySkladBulkDocumentResponseValidator bulkValidator)
    {
        this.httpClient = httpClient;
        this.tokenClient = tokenClient;
        this.rateLimiter = rateLimiter;
        this.logger = logger;
        this.responseHandler = responseHandler;
        this.singleValidator = singleValidator;
        this.bulkValidator = bulkValidator;
    }

    public MoySkladDocumentGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        ILogger<MoySkladDocumentGateway> logger)
        : this(
            httpClient,
            tokenClient,
            rateLimiter,
            logger,
            new MoySkladResponseHandler(
                new ForwardingLogger<MoySkladDocumentGateway, MoySkladResponseHandler>(logger)),
            new MoySkladSingleDocumentResponseValidator(),
            new MoySkladBulkDocumentResponseValidator())
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

        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, "GET", requestUri, documentType, null);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw responseHandler.TransportFailure(
                context, "MoySklad request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            logger.LogInformation(
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
                throw responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus,
                    $"MoySklad returned invalid document JSON: {exception.GetType().Name}.",
                    responseBody.Body, stopwatch.Elapsed);
            }

            if (payload?.Meta?.Size is null ||
                payload.Meta.Limit is null ||
                payload.Meta.Offset is null ||
                payload.Rows is null)
            {
                throw responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned incomplete document page metadata.",
                    responseBody.Body, stopwatch.Elapsed);
            }

            var rows = new List<MoySkladDocumentPageRow>(payload.Rows.Count);
            foreach (var row in payload.Rows)
            {
                if (row?.Agent?.Meta?.Href is null)
                    throw responseHandler.ValidationFailure(
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

        var context = new MoySkladRequestContext(
            accountId,
            correlationId,
            mergeJobId,
            operationId,
            request.Method.Method,
            request.RequestUri!.ToString(),
            documentType,
            isSingle ? documents[0].DocumentId : null);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw responseHandler.TransportFailure(
                context, "MoySklad document update timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            logger.LogInformation(
                "MoySklad document counterparty mutation completed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, method={Method}, chunk_size={ChunkSize}, status={StatusCode}, duration_ms={DurationMs}",
                accountId, mergeJobId, operationId, correlationId, documentType, request.Method.Method,
                documents.Count, responseBody.HttpStatus, stopwatch.Elapsed.TotalMilliseconds);
            if (string.IsNullOrWhiteSpace(json))
                throw responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned an empty document update response.", json,
                    stopwatch.Elapsed);

            JsonDocument responseDocument;
            try
            {
                responseDocument = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw responseHandler.ValidationFailure(
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
                    var validationError = singleValidator.Validate(
                        responseDocument.RootElement, documents[0], mainCounterpartyId);
                    if (validationError is not null)
                        throw responseHandler.ValidationFailure(
                            context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                    return new MoySkladDocumentChangeChunkResult([documents[0]], []);
                }

                var validation = bulkValidator.Validate(
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
                    _ = responseHandler.ValidationFailure(
                        context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                }
                return new MoySkladDocumentChangeChunkResult(validation.Succeeded, failures);
            }
        }
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

        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, requestedByUserId, cancellationToken);
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
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw responseHandler.TransportFailure(context, "MoySklad document update timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            if (string.IsNullOrWhiteSpace(json))
                throw responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned an empty document update response.", json,
                    stopwatch.Elapsed);

            JsonDocument responseDocument;
            try { responseDocument = JsonDocument.Parse(json); }
            catch (JsonException exception)
            {
                throw responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus,
                    $"MoySklad returned invalid document update JSON: {exception.GetType().Name}.", json,
                    stopwatch.Elapsed);
            }

            using (responseDocument)
            {
                var plainDocuments = documents
                    .Select(item => new MoySkladDocumentChangeItem(item.DocumentType, item.DocumentId)).ToArray();
                if (isSingle)
                {
                    var validationError = singleValidator.Validate(
                        responseDocument.RootElement, plainDocuments[0], mainCounterpartyId)
                        ?? ValidateContract(responseDocument.RootElement, documents[0].Contract);
                    if (validationError is not null)
                        throw responseHandler.ValidationFailure(
                            context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                    return new MoySkladDocumentChangeChunkResult(plainDocuments, []);
                }

                var validation = bulkValidator.Validate(responseDocument.RootElement, plainDocuments, mainCounterpartyId);
                var failedIds = validation.Failed.Select(item => item.Document.DocumentId).ToHashSet();
                var failures = validation.Failed.Select(item => new MoySkladDocumentChangeFailure(
                    item.Document.DocumentType, item.Document.DocumentId, item.Code, item.Message,
                    responseBody.HttpStatus, false, request.RequestUri!.ToString(), ValidationError: item.Message)).ToList();
                var responseById = responseDocument.RootElement.ValueKind == JsonValueKind.Array
                    ? responseDocument.RootElement.EnumerateArray()
                        .Where(item => MoySkladDocumentJson.TryReadId(item, out _))
                        .GroupBy(item => { MoySkladDocumentJson.TryReadId(item, out var id); return id; })
                        .ToDictionary(group => group.Key, group => group.First())
                    : new Dictionary<Guid, JsonElement>();
                foreach (var document in documents.Where(item => !failedIds.Contains(item.DocumentId)))
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
                    _ = responseHandler.ValidationFailure(
                        context, responseBody.HttpStatus, string.Join(" | ", failures.Select(item => item.Message)), json,
                        stopwatch.Elapsed);
                return new MoySkladDocumentChangeChunkResult(
                    plainDocuments.Where(item => !failedIds.Contains(item.DocumentId)).ToArray(), failures);
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
        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, requestedByUserId, cancellationToken);
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
        try { response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw responseHandler.TransportFailure(context, "MoySklad document update timed out.", stopwatch.Elapsed, exception); }
        catch (HttpRequestException exception)
        { throw responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception); }
        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            if (string.IsNullOrWhiteSpace(json))
                throw responseHandler.ValidationFailure(context, responseBody.HttpStatus,
                    "MoySklad returned an empty document update response.", json, stopwatch.Elapsed);
            JsonDocument responseDocument;
            try { responseDocument = JsonDocument.Parse(json); }
            catch (JsonException exception)
            { throw responseHandler.ValidationFailure(context, responseBody.HttpStatus,
                $"MoySklad returned invalid document update JSON: {exception.GetType().Name}.", json, stopwatch.Elapsed); }
            using (responseDocument)
            {
                if (isSingle)
                {
                    var validationError = singleValidator.Validate(responseDocument.RootElement, entities[0], mainCounterpartyId);
                    if (validationError is not null)
                        throw responseHandler.ValidationFailure(context, responseBody.HttpStatus, validationError, json, stopwatch.Elapsed);
                    return new MoySkladDocumentChangeChunkResult([entities[0]], []);
                }
                var validation = bulkValidator.Validate(responseDocument.RootElement, entities, mainCounterpartyId);
                var failures = validation.Failed.Select(item => new MoySkladDocumentChangeFailure(
                    item.Document.DocumentType, item.Document.DocumentId, item.Code, item.Message, responseBody.HttpStatus,
                    false, request.RequestUri!.ToString(), ValidationError: item.Message)).ToArray();
                if (failures.Length > 0)
                    _ = responseHandler.ValidationFailure(context, responseBody.HttpStatus,
                        string.Join(" | ", failures.Select(item => item.Message)), json, stopwatch.Elapsed);
                return new MoySkladDocumentChangeChunkResult(validation.Succeeded, failures);
            }
        }
    }

    private EntityReferencePayload EntityReference(string entityType, Guid id) => new(new EntityMeta(
        new Uri(httpClient.BaseAddress!, $"entity/{entityType}/{id:D}").ToString(),
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
