using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;

public interface IMoySkladPurchaseReturnGateway
{
    Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>> GetAgentAccountsAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladPurchaseReturnContract>> GetContractsAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult>> DeleteBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> documents,
        CancellationToken cancellationToken);
}

public sealed record MoySkladPurchaseReturnBatchDeleteResult(
    Guid DocumentId,
    bool Succeeded,
    string? ErrorCode = null,
    string? Error = null);

public sealed record MoySkladPurchaseReturnAgentAccount(Guid Id, bool IsDefault);

public sealed record MoySkladPurchaseReturnContract(Guid Id, bool IsDefault);

public sealed record MoySkladPurchaseReturnBatchCreateItem(
    Guid SourceDocumentId,
    string PayloadJson);

public sealed record MoySkladPurchaseReturnBatchCreateResult(
    Guid SourceDocumentId,
    Guid? DocumentId,
    string? RawJson,
    string? ErrorCode = null,
    string? Error = null);

public sealed class MoySkladPurchaseReturnGateway : IMoySkladPurchaseReturnGateway
{
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladPurchaseReturnGateway> _logger;

    public MoySkladPurchaseReturnGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladPurchaseReturnGateway> logger)
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
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateIds(purchaseReturnIds);
        var result = new Dictionary<Guid, string>();

        foreach (var batch in purchaseReturnIds.Chunk(BatchSize))
        {
            var rows = await GetBatchAsync(
                accountId, requestedByUserId, correlationId, batch, cancellationToken);
            foreach (var row in rows)
            {
                if (!result.TryAdd(row.Key, row.Value))
                    throw new InvalidOperationException(
                        $"MoySklad returned duplicate purchasereturn {row.Key:D}.");
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> GetBatchAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var filter = string.Join(';', purchaseReturnIds.Select(id => $"id={id:D}"));
        var endpoint = $"entity/purchasereturn?filter={Uri.EscapeDataString(filter)}&limit={BatchSize}";
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "purchasereturn", null);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad purchasereturn request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, document_count={DocumentCount}, limit={Limit}",
            accountId, requestedByUserId, correlationId, purchaseReturnIds.Count, BatchSize);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad purchasereturn request timed out.", stopwatch.Elapsed, exception);
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
            return ParseResponse(responseBody.Body, responseBody.HttpStatus, context, stopwatch.Elapsed, purchaseReturnIds);
        }
    }

    public async Task<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>> GetAgentAccountsAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (mainCounterpartyId == Guid.Empty)
            throw new ArgumentException("A non-empty main counterparty id is required.", nameof(mainCounterpartyId));

        var endpoint = $"entity/counterparty/{mainCounterpartyId:D}/accounts?limit={BatchSize}";
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Get, endpoint, null, cancellationToken,
            "counterparty", mainCounterpartyId);
        return ParseCounterpartyChildren<MoySkladPurchaseReturnAgentAccount>(
            response.Body,
            accountId,
            correlationId,
            endpoint,
            response,
            (id, isDefault) => new MoySkladPurchaseReturnAgentAccount(id, isDefault));
    }

    public async Task<IReadOnlyList<MoySkladPurchaseReturnContract>> GetContractsAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (mainCounterpartyId == Guid.Empty)
            throw new ArgumentException("A non-empty main counterparty id is required.", nameof(mainCounterpartyId));

        var agentHref = new Uri(_httpClient.BaseAddress!, $"entity/counterparty/{mainCounterpartyId:D}").AbsoluteUri;
        var filter = Uri.EscapeDataString($"agent={agentHref}");
        var endpoint = $"entity/contract?filter={filter}&limit={BatchSize}";
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Get, endpoint, null, cancellationToken,
            "contract", null);
        return ParseCounterpartyChildren<MoySkladPurchaseReturnContract>(
            response.Body,
            accountId,
            correlationId,
            endpoint,
            response,
            (id, isDefault) => new MoySkladPurchaseReturnContract(id, isDefault));
    }

    private IReadOnlyList<T> ParseCounterpartyChildren<T>(
        string body,
        Guid accountId,
        string correlationId,
        string endpoint,
        MoySkladResponseBody response,
        Func<Guid, bool, T> factory)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new JsonException("The MoySklad response does not contain rows.");

            var ids = new HashSet<Guid>();
            var result = new List<T>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var id) || id == Guid.Empty ||
                    !ids.Add(id))
                    throw new JsonException("The MoySklad response contains an invalid or duplicate id.");

                var isDefault = row.TryGetProperty("default", out var defaultProperty) &&
                    defaultProperty.ValueKind == JsonValueKind.True;
                result.Add(factory(id, isDefault));
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId,
                correlationId,
                endpoint,
                response,
                $"MoySklad returned invalid purchasereturn reference data: {exception.Message}");
        }
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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new JsonException("The purchasereturn response does not contain rows.");

            var requested = requestedIds.ToHashSet();
            var result = new Dictionary<Guid, string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var id) ||
                    id == Guid.Empty ||
                    !requested.Contains(id) ||
                    !result.TryAdd(id, row.GetRawText()))
                    throw new JsonException("The purchasereturn response contains an invalid, unexpected or duplicate document.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                context,
                statusCode,
                $"MoySklad returned invalid purchasereturn data: {exception.Message}",
                body,
                duration);
        }
    }

    private static void ValidateIds(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0 || ids.Count > BatchSize || ids.Any(id => id == Guid.Empty) ||
            ids.Distinct().Count() != ids.Count)
            throw new ArgumentException(
                $"A unique non-empty purchasereturn id list of 1 to {BatchSize} items is required.", nameof(ids));
    }

    public async Task<IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult>> DeleteBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        ValidateIds(purchaseReturnIds);
        const string endpoint = "entity/purchasereturn/delete";
        var payload = JsonSerializer.Serialize(purchaseReturnIds.Select(id => new
        {
            meta = new
            {
                href = new Uri(_httpClient.BaseAddress!, $"entity/purchasereturn/{id:D}").AbsoluteUri,
                type = "purchasereturn",
                mediaType = "application/json"
            }
        }));
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Post, endpoint, payload, cancellationToken);
        return ParseDeleteResponse(accountId, correlationId, endpoint, response, purchaseReturnIds);
    }

    public async Task<IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count is < 1 or > BatchSize ||
            documents.Any(item => item.SourceDocumentId == Guid.Empty || string.IsNullOrWhiteSpace(item.PayloadJson)) ||
            documents.Select(item => item.SourceDocumentId).Distinct().Count() != documents.Count)
            throw new ArgumentException(
                $"A unique non-empty purchasereturn creation batch of 1 to {BatchSize} items is required.", nameof(documents));

        const string endpoint = "entity/purchasereturn/batch";
        var payload = "[" + string.Join(',', documents.Select(item => item.PayloadJson)) + "]";
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Post, endpoint, payload, cancellationToken);
        return ParseCreateResponse(accountId, correlationId, endpoint, response, documents);
    }

    private async Task<MoySkladResponseBody> SendJsonAsync(
        Guid accountId,
        string correlationId,
        HttpMethod method,
        string endpoint,
        string? payload,
        CancellationToken cancellationToken,
        string entityType = "purchasereturn",
        Guid? entityId = null)
    {
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, method.Method, endpoint, entityType, entityId);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);
        using var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad purchasereturn mutation request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(
                accountId, MoySkladRateLimitObservationParser.Parse(response), cancellationToken);
            return await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
        }
    }

    private IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult> ParseDeleteResponse(
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

            var results = new List<MoySkladPurchaseReturnBatchDeleteResult>(requestedIds.Count);
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var documentId = TryReadId(row) ?? requestedIds[index];
                if (documentId != requestedIds[index])
                    throw new JsonException("The batch delete response has an unexpected document id.");
                var error = TryReadError(row);
                results.Add(new MoySkladPurchaseReturnBatchDeleteResult(
                    documentId, error is null, error?.Code, error?.Message));
                index++;
            }
            return results;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId, correlationId, endpoint, response,
                $"MoySklad returned invalid purchasereturn batch delete data: {exception.Message}");
        }
    }

    private IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult> ParseCreateResponse(
        Guid accountId,
        string correlationId,
        string endpoint,
        MoySkladResponseBody response,
        IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> requested)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != requested.Count)
                throw new JsonException("The batch create response does not match the request size.");

            var rows = document.RootElement.EnumerateArray().ToArray();
            var results = new List<MoySkladPurchaseReturnBatchCreateResult>(requested.Count);
            for (var index = 0; index < requested.Count; index++)
            {
                var row = rows[index];
                var error = TryReadError(row);
                var documentId = TryReadGuidProperty(row, "id");
                if (error is null && (documentId is null || documentId == Guid.Empty))
                    error = ("PURCHASERETURN_CREATE_RESPONSE_INVALID", "MoySklad did not return a valid document id.");
                results.Add(new MoySkladPurchaseReturnBatchCreateResult(
                    requested[index].SourceDocumentId,
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
                accountId, correlationId, endpoint, response,
                $"MoySklad returned invalid purchasereturn batch create data: {exception.Message}");
        }
    }

    private EgressException ValidationFailure(
        Guid accountId,
        string correlationId,
        string endpoint,
        MoySkladResponseBody response,
        string message) => _responseHandler.ValidationFailure(
        new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Post.Method, endpoint, "purchasereturn", null),
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
            return ("PURCHASERETURN_BATCH_ITEM_REJECTED", "MoySklad rejected the purchasereturn batch item.");
        var code = first.TryGetProperty("code", out var codeValue)
            ? codeValue.ToString()
            : "PURCHASERETURN_BATCH_ITEM_REJECTED";
        var message = first.TryGetProperty("error", out var messageValue)
            ? messageValue.ToString()
            : "MoySklad rejected the purchasereturn batch item.";
        return (code, message);
    }
}
