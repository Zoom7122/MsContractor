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

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;

public sealed record MoySkladFactureOutBatchDeleteResult(
    Guid SourceDocumentId,
    bool Succeeded,
    string? ErrorCode = null,
    string? Error = null);

public sealed record MoySkladFactureOutBatchCreateItem(
    Guid SourceDocumentId,
    Guid SyncId,
    string PayloadJson);

public sealed record MoySkladFactureOutBatchCreateResult(
    int ResponseIndex,
    Guid? DocumentId,
    Guid? ReturnedSyncId,
    string? DocumentType,
    string? ErrorCode = null,
    string? Error = null);

public interface IMoySkladFactureOutGateway
{
    Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladFactureOutBatchDeleteResult>> DeleteBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<MoySkladFactureOutBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladFactureOutBatchCreateItem> documents,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

public sealed class MoySkladFactureOutGateway : IMoySkladFactureOutGateway
{
    public const int BatchSize = 1000;

    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladFactureOutGateway> _logger;

    public MoySkladFactureOutGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladFactureOutGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        ValidateIds(factureOutIds);
        var endpoint = BuildEndpoint(factureOutIds);
        var response = await SendGetAsync(accountId, correlationId, endpoint, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The response does not contain rows.");
            }

            var requestedIds = factureOutIds.ToHashSet();
            var result = new Dictionary<Guid, string>();
            foreach (var row in rows.EnumerateArray())
            {
                var documentId = ReadId(row);
                ValidateTypeIfPresent(row);
                if (!requestedIds.Contains(documentId) || !result.TryAdd(documentId, row.GetRawText()))
                    throw new InvalidOperationException(
                        "MoySklad returned an unexpected or duplicate factureout document.");
            }

            _logger.LogInformation(
                "MoySklad factureout batch received: account_id={AccountId}, requested_count={RequestedCount}, returned_count={ReturnedCount}, correlation_id={CorrelationId}",
                accountId,
                factureOutIds.Count,
                result.Count,
                correlationId);

            return result;
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                new MoySkladRequestContext(
                    accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "factureout", null),
                response.HttpStatus,
                $"MoySklad returned invalid factureout data: {exception.Message}",
                response.Body,
                TimeSpan.Zero);
        }
        catch (InvalidOperationException exception)
        {
            throw new EgressException(
                StatusCodes.Status502BadGateway,
                "MOYSKLAD_RESPONSE_VALIDATION_FAILED",
                exception.Message,
                exception);
        }
    }

    public async Task<IReadOnlyList<MoySkladFactureOutBatchDeleteResult>> DeleteBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        ValidateIds(factureOutIds);
        const string endpoint = "entity/factureout/delete";
        var payload = JsonSerializer.Serialize(factureOutIds.Select(id => new
        {
            meta = new
            {
                href = new Uri(_httpClient.BaseAddress!, $"entity/factureout/{id:D}").AbsoluteUri,
                type = "factureout",
                mediaType = "application/json"
            }
        }));
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Post, endpoint, payload, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > factureOutIds.Count)
            {
                throw new JsonException("The factureout delete response is not a matching array.");
            }

            var result = new List<MoySkladFactureOutBatchDeleteResult>(document.RootElement.GetArrayLength());
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    throw new JsonException("The factureout delete response contains a non-object item.");

                var id = TryReadGuidProperty(row, "id") ?? TryReadGuidFromMeta(row) ?? factureOutIds[index];
                if (id != factureOutIds[index])
                    throw new JsonException("The factureout delete response has an unexpected id.");

                var error = TryReadError(row);
                result.Add(new MoySkladFactureOutBatchDeleteResult(
                    id, error is null, error?.Code, error?.Message));
                index++;
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId,
                correlationId,
                HttpMethod.Post,
                endpoint,
                response,
                $"MoySklad returned invalid factureout delete data: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<MoySkladFactureOutBatchCreateResult>> CreateBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladFactureOutBatchCreateItem> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count is < 1 or > BatchSize ||
            documents.Any(item => item.SourceDocumentId == Guid.Empty || item.SyncId == Guid.Empty ||
                                  string.IsNullOrWhiteSpace(item.PayloadJson)) ||
            documents.Select(item => item.SourceDocumentId).Distinct().Count() != documents.Count ||
            documents.Select(item => item.SyncId).Distinct().Count() != documents.Count)
        {
            throw new ArgumentException("A unique non-empty factureout creation batch is required.", nameof(documents));
        }

        const string endpoint = "entity/factureout/batch";
        var payload = "[" + string.Join(',', documents.Select(item => item.PayloadJson)) + "]";
        var response = await SendJsonAsync(
            accountId, correlationId, HttpMethod.Post, endpoint, payload, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("The factureout create response is not an array.");

            var result = new List<MoySkladFactureOutBatchCreateResult>();
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    throw new JsonException("The factureout create response contains a non-object item.");

                var error = TryReadError(row);
                result.Add(new MoySkladFactureOutBatchCreateResult(
                    index++,
                    TryReadGuidProperty(row, "id"),
                    TryReadGuidProperty(row, "syncId"),
                    TryReadType(row),
                    error?.Code,
                    error?.Message));
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw ValidationFailure(
                accountId,
                correlationId,
                HttpMethod.Post,
                endpoint,
                response,
                $"MoySklad returned invalid factureout create data: {exception.Message}");
        }
    }

    private async Task<MoySkladResponseBody> SendGetAsync(
        Guid accountId,
        string correlationId,
        string endpoint,
        CancellationToken cancellationToken)
        => await SendJsonAsync(accountId, correlationId, HttpMethod.Get, endpoint, null, cancellationToken);

    private async Task<MoySkladResponseBody> SendJsonAsync(
        Guid accountId,
        string correlationId,
        HttpMethod method,
        string endpoint,
        string? payload,
        CancellationToken cancellationToken)
    {
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, method.Method, endpoint, "factureout", null);
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
                context, "MoySklad factureout request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(
                accountId,
                MoySkladRateLimitObservationParser.Parse(response),
                cancellationToken);
            return await _responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken);
        }
    }

    private Exception ValidationFailure(
        Guid accountId,
        string correlationId,
        HttpMethod method,
        string endpoint,
        MoySkladResponseBody response,
        string message) =>
        _responseHandler.ValidationFailure(
            new MoySkladRequestContext(
                accountId, correlationId, null, null, method.Method, endpoint, "factureout", null),
            response.HttpStatus,
            message,
            response.Body,
            TimeSpan.Zero);

    private static string BuildEndpoint(IReadOnlyList<Guid> factureOutIds)
    {
        var filter = string.Join(';', factureOutIds.Select(id => $"id={id:D}"));
        return $"entity/factureout?filter={Uri.EscapeDataString(filter)}&limit={BatchSize}";
    }

    private static Guid ReadId(JsonElement row)
    {
        if (row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String &&
            Guid.TryParse(idElement.GetString(), out var id) &&
            id != Guid.Empty)
        {
            return id;
        }

        throw new InvalidOperationException("MoySklad factureout has no valid id.");
    }

    private static Guid? TryReadGuidProperty(JsonElement row, string propertyName)
    {
        if (row.ValueKind == JsonValueKind.Object &&
            row.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            Guid.TryParse(value.GetString(), out var id) &&
            id != Guid.Empty)
        {
            return id;
        }

        return null;
    }

    private static Guid? TryReadGuidFromMeta(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("href", out var href) ||
            href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var id) && id != Guid.Empty
            ? id
            : null;
    }

    private static string? TryReadType(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty("meta", out var meta) &&
        meta.ValueKind == JsonValueKind.Object &&
        meta.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static (string Code, string Message)? TryReadError(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0)
        {
            return null;
        }

        var first = errors.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            return ("FACTUREOUT_BATCH_ITEM_REJECTED", "MoySklad rejected the factureout batch item.");

        return (
            first.TryGetProperty("code", out var code) ? code.ToString() : "FACTUREOUT_BATCH_ITEM_REJECTED",
            first.TryGetProperty("error", out var error)
                ? error.ToString()
                : "MoySklad rejected the factureout batch item.");
    }

    private static void ValidateTypeIfPresent(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("type", out var type))
        {
            return;
        }

        if (type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "factureout", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MoySklad returned a document that is not factureout.");
        }
    }

    private static void ValidateIds(IReadOnlyList<Guid> factureOutIds)
    {
        if (factureOutIds.Count is < 1 or > BatchSize ||
            factureOutIds.Any(id => id == Guid.Empty) ||
            factureOutIds.Distinct().Count() != factureOutIds.Count)
        {
            throw new ArgumentException(
                $"A unique non-empty factureout id batch of 1 to {BatchSize} items is required.",
                nameof(factureOutIds));
        }
    }
}
