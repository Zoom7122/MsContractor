using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;

public interface IMoySkladSalesReturnGateway
{
    Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladSalesReturnAgentAccount>> GetAgentAccountsAsync(
        Guid accountId,
        Guid mainAgentId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladSalesReturnBatchDeleteResult>> DeleteBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladSalesReturnBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladSalesReturnBatchCreateItem> documents,
        CancellationToken cancellationToken);
}

public sealed record MoySkladSalesReturnAgentAccount(Guid Id, bool IsDefault);

public sealed record MoySkladSalesReturnBatchDeleteResult(
    Guid DocumentId,
    bool Succeeded,
    string? ErrorCode = null,
    string? Error = null);

public sealed record MoySkladSalesReturnBatchCreateItem(
    Guid SourceDocumentId,
    Guid SyncId,
    string PayloadJson);

public sealed record MoySkladSalesReturnBatchCreateResult(
    Guid SourceDocumentId,
    Guid SyncId,
    Guid? DocumentId,
    string? RawJson,
    string? ErrorCode = null,
    string? Error = null);

public sealed class MoySkladSalesReturnGateway : IMoySkladSalesReturnGateway
{
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladSalesReturnGateway> _logger;

    public MoySkladSalesReturnGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladSalesReturnGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
        _logger = logger;
    }

    public const int BatchSize = 1000;

    public async Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateIds(salesReturnIds);

        var filter = string.Join(';', salesReturnIds.Select(id => $"id={id:D}"));
        var endpoint = $"entity/salesreturn?filter={Uri.EscapeDataString(filter)}&limit={BatchSize}";
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "salesreturn", null);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad salesreturn request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_count={DocumentCount}, limit={Limit}",
            accountId, requestedByUserId, correlationId, salesReturnIds.Count, BatchSize);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad salesreturn request timed out.", stopwatch.Elapsed, exception);
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
                "MoySklad salesreturn response received: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_count={DocumentCount}, status={StatusCode}, duration_ms={DurationMs}",
                accountId, requestedByUserId, correlationId, salesReturnIds.Count,
                responseBody.HttpStatus, stopwatch.Elapsed.TotalMilliseconds);

            return ParseResponse(
                responseBody.Body, responseBody.HttpStatus, context, stopwatch.Elapsed, salesReturnIds);
        }
    }

    public async Task<IReadOnlyList<MoySkladSalesReturnAgentAccount>> GetAgentAccountsAsync(
        Guid accountId,
        Guid mainAgentId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (mainAgentId == Guid.Empty)
            throw new ArgumentException("A non-empty main agent id is required.", nameof(mainAgentId));

        var endpoint = $"entity/counterparty/{mainAgentId:D}/accounts?limit={BatchSize}";
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Get, endpoint, "counterparty", mainAgentId, null, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new JsonException("The counterparty accounts response does not contain rows.");

            var accounts = new List<MoySkladSalesReturnAgentAccount>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var id) || id == Guid.Empty)
                    throw new JsonException("The counterparty accounts response contains an invalid id.");
                var isDefault = row.TryGetProperty("default", out var defaultProperty) &&
                    defaultProperty.ValueKind == JsonValueKind.True;
                accounts.Add(new MoySkladSalesReturnAgentAccount(id, isDefault));
            }

            if (accounts.Select(item => item.Id).Distinct().Count() != accounts.Count)
                throw new JsonException("The counterparty accounts response contains duplicate ids.");
            return accounts;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId, correlationId, endpoint, "counterparty", mainAgentId, response,
                $"MoySklad returned invalid counterparty accounts: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<MoySkladSalesReturnBatchDeleteResult>> DeleteBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateIds(salesReturnIds);
        const string endpoint = "entity/salesreturn/delete";
        var payload = JsonSerializer.Serialize(salesReturnIds.Select(id => new
        {
            meta = new
            {
                href = new Uri(_httpClient.BaseAddress!, $"entity/salesreturn/{id:D}").AbsoluteUri,
                type = "salesreturn",
                mediaType = "application/json"
            }
        }));
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Post, endpoint, "salesreturn", null, payload, cancellationToken);
        return ParseDeleteResponse(accountId, correlationId, endpoint, response, salesReturnIds);
    }

    public async Task<IReadOnlyList<MoySkladSalesReturnBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladSalesReturnBatchCreateItem> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count is < 1 or > BatchSize ||
            documents.Any(item => item.SourceDocumentId == Guid.Empty || item.SyncId == Guid.Empty ||
                                  string.IsNullOrWhiteSpace(item.PayloadJson)) ||
            documents.Select(item => item.SourceDocumentId).Distinct().Count() != documents.Count ||
            documents.Select(item => item.SyncId).Distinct().Count() != documents.Count)
            throw new ArgumentException("A unique non-empty salesreturn creation batch is required.", nameof(documents));

        const string endpoint = "entity/salesreturn/batch";
        var payload = "[" + string.Join(',', documents.Select(item => item.PayloadJson)) + "]";
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Post, endpoint, "salesreturn", null, payload, cancellationToken);
        return ParseCreateResponse(accountId, correlationId, endpoint, response, documents);
    }

    private IReadOnlyDictionary<Guid, string> ParseResponse(
        string body,
        int statusCode,
        MoySkladRequestContext context,
        TimeSpan duration,
        IReadOnlyList<Guid> requestedIds)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new JsonException("The salesreturn response does not contain rows.");

            var requested = requestedIds.ToHashSet();
            var result = new Dictionary<Guid, string>(requested.Count);
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var documentId) ||
                    documentId == Guid.Empty)
                    throw new JsonException("The salesreturn response contains a document without a valid id.");
                if (!requested.Contains(documentId))
                    throw new JsonException("The salesreturn response contains an unexpected document id.");
                if (!result.TryAdd(documentId, row.GetRawText()))
                    throw new JsonException("The salesreturn response contains a duplicate document id.");
            }

            if (result.Count != requested.Count)
                throw new JsonException("The salesreturn response does not contain all requested documents.");

            return result;
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                context, statusCode,
                $"MoySklad returned an incomplete salesreturn response: {exception.Message}",
                body, duration);
        }
    }

    private static void ValidateIds(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || ids.Count > BatchSize ||
            ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException(
                $"A unique non-empty salesreturn id list of 1 to {BatchSize} items is required.", nameof(ids));
    }

    private async Task<MoySkladResponseBody> SendJsonAsync(
        Guid accountId,
        string correlationId,
        HttpMethod method,
        string endpoint,
        string entityType,
        Guid? entityId,
        string? payload,
        CancellationToken cancellationToken)
    {
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, method.Method, endpoint, entityType, entityId);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        if (payload is not null)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            _logger.LogInformation(
                "Sending MoySklad salesreturn JSON request: account_id={AccountId}, correlation_id={CorrelationId}, http_method={HttpMethod}, endpoint={Endpoint}, payload={Payload}",
                accountId, correlationId, method.Method, endpoint, payload);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(context, "MoySklad salesreturn request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(accountId, MoySkladRateLimitObservationParser.Parse(response), cancellationToken);
            return await _responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
        }
    }

    private IReadOnlyList<MoySkladSalesReturnBatchDeleteResult> ParseDeleteResponse(
        Guid accountId,
        string correlationId,
        string endpoint,
        MoySkladResponseBody response,
        IReadOnlyList<Guid> requestedIds)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != requestedIds.Count)
                throw new JsonException("The batch delete response does not match the request size.");

            var results = new List<MoySkladSalesReturnBatchDeleteResult>(requestedIds.Count);
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var documentId = TryReadId(row) ?? requestedIds[index];
                if (documentId != requestedIds[index])
                    throw new JsonException("The batch delete response has an unexpected document id.");
                var error = TryReadError(row);
                results.Add(new MoySkladSalesReturnBatchDeleteResult(documentId, error is null, error?.Code, error?.Message));
                index++;
            }
            return results;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId, correlationId, endpoint, "salesreturn", null, response,
                $"MoySklad returned invalid salesreturn batch delete data: {exception.Message}");
        }
    }

    private IReadOnlyList<MoySkladSalesReturnBatchCreateResult> ParseCreateResponse(
        Guid accountId,
        string correlationId,
        string endpoint,
        MoySkladResponseBody response,
        IReadOnlyList<MoySkladSalesReturnBatchCreateItem> requested)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != requested.Count)
                throw new JsonException("The batch create response does not match the request size.");

            var responseRows = document.RootElement.EnumerateArray().ToArray();
            var results = new List<MoySkladSalesReturnBatchCreateResult>(requested.Count);
            for (var index = 0; index < requested.Count; index++)
            {
                var item = requested[index];
                var matching = responseRows
                    .Where(row => TryReadGuidProperty(row, "syncId") == item.SyncId)
                    .ToArray();
                var row = matching.Length == 1 ? matching[0] : responseRows[index];
                var error = TryReadError(row);
                var documentId = TryReadGuidProperty(row, "id");
                if (error is null && (documentId is null || documentId == Guid.Empty))
                    error = ("SALESRETURN_CREATE_RESPONSE_INVALID", "MoySklad did not return a valid document id.");
                results.Add(new MoySkladSalesReturnBatchCreateResult(
                    item.SourceDocumentId,
                    item.SyncId,
                    documentId,
                    error is null ? row.GetRawText() : null,
                    error?.Code,
                    error?.Message));
            }
            return results;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId, correlationId, endpoint, "salesreturn", null, response,
                $"MoySklad returned invalid salesreturn batch create data: {exception.Message}");
        }
    }

    private EgressException ValidationFailure(
        Guid accountId,
        string correlationId,
        string endpoint,
        string entityType,
        Guid? entityId,
        MoySkladResponseBody response,
        string message) => _responseHandler.ValidationFailure(
        new MoySkladRequestContext(accountId, correlationId, null, null, HttpMethod.Post.Method, endpoint, entityType, entityId),
        response.HttpStatus,
        message,
        response.Body,
        TimeSpan.Zero);

    private static Guid? TryReadId(JsonElement row) =>
        TryReadGuidProperty(row, "id") ?? TryReadGuidFromMeta(row);

    private static Guid? TryReadGuidFromMeta(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
            return null;
        return Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var id) ? id : null;
    }

    private static Guid? TryReadGuidProperty(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;

    private static (string Code, string Message)? TryReadError(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            return null;
        var first = errors.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            return ("SALESRETURN_BATCH_ITEM_REJECTED", "MoySklad rejected the salesreturn batch item.");
        var code = first.TryGetProperty("code", out var codeValue) ? codeValue.ToString() : "SALESRETURN_BATCH_ITEM_REJECTED";
        var message = first.TryGetProperty("error", out var messageValue) ? messageValue.ToString() : "MoySklad rejected the salesreturn batch item.";
        return (code, message);
    }
}
