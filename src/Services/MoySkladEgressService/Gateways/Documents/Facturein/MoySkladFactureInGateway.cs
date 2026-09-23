using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;

public sealed record FactureInBaseReference(string Type, Guid DocumentId);

public sealed record MoySkladFactureInBatchDeleteResult(
    Guid SourceDocumentId, bool Succeeded, string? ErrorCode = null, string? Error = null);

public sealed record MoySkladFactureInBatchCreateItem(
    Guid SourceDocumentId, Guid SyncId, string PayloadJson);

public sealed record MoySkladFactureInBatchCreateResult(
    int ResponseIndex,
    Guid? DocumentId,
    Guid? ReturnedSyncId,
    string? DocumentType,
    string? ErrorCode = null,
    string? Error = null);

public interface IMoySkladFactureInGateway
{
    Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId,
        string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken);

    Task<IReadOnlySet<FactureInBaseReference>> GetExistingBasesAsync(Guid accountId,
        string correlationId, IReadOnlyCollection<FactureInBaseReference> references,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<MoySkladFactureInBatchDeleteResult>> DeleteBatchAsync(Guid accountId,
        string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<MoySkladFactureInBatchCreateResult>> CreateBatchAsync(Guid accountId,
        string correlationId, IReadOnlyList<MoySkladFactureInBatchCreateItem> documents,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyDictionary<Guid, Guid>> FindBySyncIdsAsync(Guid accountId,
        string correlationId, IReadOnlyList<Guid> syncIds, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public sealed class MoySkladFactureInGateway : IMoySkladFactureInGateway
{
    public const int BatchSize = 1000;
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;

    public MoySkladFactureInGateway(HttpClient httpClient, IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter, IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladFactureInGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId,
        string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken)
    {
        if (factureInIds.Count > BatchSize)
            throw new ArgumentOutOfRangeException(nameof(factureInIds), $"A facturein batch cannot exceed {BatchSize} documents.");
        ValidateIds(factureInIds, "facturein");
        var rows = await GetRowsAsync(accountId, correlationId, "facturein", factureInIds, "id", cancellationToken);
        var requested = factureInIds.ToHashSet();
        var result = new Dictionary<Guid, string>();
        try
        {
            foreach (var row in rows)
            {
                var id = RequireId(row, "facturein");
                ValidateTypeIfPresent(row, "facturein");
                if (!requested.Contains(id) || !result.TryAdd(id, row.GetRawText()))
                    throw new InvalidOperationException("MoySklad returned an unexpected or duplicate facturein document.");
            }
        }
        catch (InvalidOperationException exception)
        {
            throw new EgressException(502, "MOYSKLAD_RESPONSE_VALIDATION_FAILED", exception.Message, exception);
        }
        return result;
    }

    public async Task<IReadOnlySet<FactureInBaseReference>> GetExistingBasesAsync(Guid accountId,
        string correlationId, IReadOnlyCollection<FactureInBaseReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Any(item => item.DocumentId == Guid.Empty || item.Type is not ("supply" or "paymentout" or "cashout")))
            throw new ArgumentException("Only non-empty supply, paymentout and cashout base references are supported.", nameof(references));

        var result = new HashSet<FactureInBaseReference>();
        foreach (var group in references.Distinct().GroupBy(item => item.Type, StringComparer.Ordinal))
        foreach (var batch in group.Select(item => item.DocumentId).Chunk(BatchSize))
        {
            var rows = await GetRowsAsync(accountId, correlationId, group.Key, batch, "id", cancellationToken);
            foreach (var row in rows)
            {
                var id = RequireId(row, group.Key);
                RequireType(row, group.Key);
                result.Add(new FactureInBaseReference(group.Key, id));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<MoySkladFactureInBatchDeleteResult>> DeleteBatchAsync(Guid accountId,
        string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken)
    {
        ValidateIds(factureInIds, "facturein");
        const string endpoint = "entity/facturein/delete";
        var payload = JsonSerializer.Serialize(factureInIds.Select(id => new { meta = new
        {
            href = new Uri(_httpClient.BaseAddress!, $"entity/facturein/{id:D}").AbsoluteUri,
            type = "facturein", mediaType = "application/json"
        }}));
        var response = await SendJsonAsync(accountId, correlationId, HttpMethod.Post, endpoint, "facturein", payload, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != factureInIds.Count)
                throw new JsonException("The facturein delete response does not match request size.");
            var result = new List<MoySkladFactureInBatchDeleteResult>(factureInIds.Count);
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var id = TryReadId(row) ?? factureInIds[index];
                if (id != factureInIds[index])
                    throw new JsonException("The facturein delete response has an unexpected id.");
                var error = TryReadError(row);
                result.Add(new MoySkladFactureInBatchDeleteResult(id, error is null, error?.Code, error?.Message));
                index++;
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(accountId, correlationId, endpoint, response,
                $"MoySklad returned invalid facturein delete data: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<MoySkladFactureInBatchCreateResult>> CreateBatchAsync(Guid accountId,
        string correlationId, IReadOnlyList<MoySkladFactureInBatchCreateItem> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count is < 1 or > BatchSize ||
            documents.Any(item => item.SourceDocumentId == Guid.Empty || item.SyncId == Guid.Empty || string.IsNullOrWhiteSpace(item.PayloadJson)) ||
            documents.Select(item => item.SourceDocumentId).Distinct().Count() != documents.Count ||
            documents.Select(item => item.SyncId).Distinct().Count() != documents.Count)
            throw new ArgumentException("A unique non-empty facturein creation batch is required.", nameof(documents));

        const string endpoint = "entity/facturein/batch";
        var payload = "[" + string.Join(',', documents.Select(item => item.PayloadJson)) + "]";
        var response = await SendJsonAsync(accountId, correlationId, HttpMethod.Post, endpoint, "facturein", payload, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("The facturein create response is not an array.");
            var result = new List<MoySkladFactureInBatchCreateResult>();
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    throw new JsonException("The facturein create response contains a non-object item.");
                var error = TryReadError(row);
                result.Add(new MoySkladFactureInBatchCreateResult(index++, TryReadGuidProperty(row, "id"),
                    TryReadGuidProperty(row, "syncId"), TryReadType(row), error?.Code, error?.Message));
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(accountId, correlationId, endpoint, response,
                $"MoySklad returned invalid facturein create data: {exception.Message}");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> FindBySyncIdsAsync(Guid accountId,
        string correlationId, IReadOnlyList<Guid> syncIds, CancellationToken cancellationToken)
    {
        ValidateIds(syncIds, "sync");
        var rows = await GetRowsAsync(accountId, correlationId, "facturein", syncIds, "syncId", cancellationToken);
        var result = new Dictionary<Guid, Guid>();
        foreach (var row in rows)
        {
            RequireType(row, "facturein");
            var syncId = RequireGuidProperty(row, "syncId", "facturein");
            var documentId = RequireId(row, "facturein");
            if (!syncIds.Contains(syncId) || !result.TryAdd(syncId, documentId))
                throw new InvalidOperationException("MoySklad returned an unexpected or duplicate facturein syncId.");
        }
        return result;
    }

    private async Task<IReadOnlyList<JsonElement>> GetRowsAsync(Guid accountId, string correlationId,
        string entityType, IReadOnlyList<Guid> values, string filterName, CancellationToken cancellationToken)
    {
        ValidateIds(values, filterName);
        var filter = string.Join(';', values.Select(value => $"{filterName}={value:D}"));
        var endpoint = $"entity/{entityType}?filter={Uri.EscapeDataString(filter)}&limit={BatchSize}";
        var response = await SendJsonAsync(accountId, correlationId, HttpMethod.Get, endpoint, entityType, null, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new JsonException("The response does not contain rows.");
            return rows.EnumerateArray().Select(row => row.Clone()).ToArray();
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(accountId, correlationId, endpoint, response,
                $"MoySklad returned invalid {entityType} data: {exception.Message}");
        }
    }

    private async Task<MoySkladResponseBody> SendJsonAsync(Guid accountId, string correlationId,
        HttpMethod method, string endpoint, string entityType, string? payload, CancellationToken cancellationToken)
    {
        var context = new MoySkladRequestContext(accountId, correlationId, null, null, method.Method, endpoint, entityType, null);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        if (payload is not null)
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw _responseHandler.TransportFailure(context, "MoySklad facturein request timed out.", stopwatch.Elapsed, exception); }
        catch (HttpRequestException exception)
        { throw _responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception); }
        using (response)
        {
            await _rateLimiter.ObserveAsync(accountId, MoySkladRateLimitObservationParser.Parse(response), cancellationToken);
            return await _responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
        }
    }

    private Exception ValidationFailure(Guid accountId, string correlationId, string endpoint, MoySkladResponseBody response, string message) =>
        _responseHandler.ValidationFailure(new MoySkladRequestContext(accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "facturein", null),
            response.HttpStatus, message, response.Body, TimeSpan.Zero);

    private static Guid RequireId(JsonElement row, string entityType) => TryReadGuidProperty(row, "id") ?? throw new InvalidOperationException($"MoySklad {entityType} has no valid id.");
    private static Guid RequireGuidProperty(JsonElement row, string property, string entityType) => TryReadGuidProperty(row, property) ?? throw new InvalidOperationException($"MoySklad {entityType} has no valid {property}.");
    private static void RequireType(JsonElement row, string expectedType)
    {
        if (!string.Equals(TryReadType(row), expectedType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MoySklad returned a document that is not {expectedType}.");
    }
    private static void ValidateTypeIfPresent(JsonElement row, string expectedType)
    {
        var type = TryReadType(row);
        if (type is not null && !string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MoySklad returned a document that is not {expectedType}.");
    }
    private static Guid? TryReadId(JsonElement row) => TryReadGuidProperty(row, "id") ?? TryReadGuidFromMeta(row);
    private static Guid? TryReadGuidFromMeta(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String || !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri)) return null;
        return Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var id) ? id : null;
    }
    private static Guid? TryReadGuidProperty(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty ? id : null;
    private static string? TryReadType(JsonElement row) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
        meta.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
    private static (string Code, string Message)? TryReadError(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return null;
        var first = errors.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return ("FACTUREIN_BATCH_ITEM_REJECTED", "MoySklad rejected the facturein batch item.");
        return (first.TryGetProperty("code", out var code) ? code.ToString() : "FACTUREIN_BATCH_ITEM_REJECTED",
            first.TryGetProperty("error", out var error) ? error.ToString() : "MoySklad rejected the facturein batch item.");
    }
    private static void ValidateIds(IReadOnlyList<Guid> ids, string name)
    {
        if (ids.Count is < 1 or > BatchSize || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException($"A unique non-empty {name} id batch of 1 to {BatchSize} items is required.", name);
    }
}
