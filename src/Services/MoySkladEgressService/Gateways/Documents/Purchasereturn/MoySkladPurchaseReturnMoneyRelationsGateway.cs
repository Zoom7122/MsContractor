using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;

public interface IMoySkladPurchaseReturnMoneyRelationsGateway
{
    Task<string> GetPaymentInAsync(
        Guid accountId,
        Guid documentId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<string> GetCashInAsync(
        Guid accountId,
        Guid documentId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> PaymentInBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> CashInBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
        CancellationToken cancellationToken);
}

public sealed record MoySkladPurchaseReturnMoneyRelationBatchItem(
    Guid DocumentId,
    string PayloadJson);

public sealed record MoySkladPurchaseReturnMoneyRelationBatchResult(
    Guid DocumentId,
    string RawJson,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool Succeeded => ErrorCode is null;
}

public sealed class MoySkladPurchaseReturnMoneyRelationsGateway
    : IMoySkladPurchaseReturnMoneyRelationsGateway
{
    public const int BatchSize = 1000;

    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladPurchaseReturnMoneyRelationsGateway> _logger;

    public MoySkladPurchaseReturnMoneyRelationsGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladPurchaseReturnMoneyRelationsGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
        _logger = logger;
    }

    public Task<string> GetPaymentInAsync(
        Guid accountId,
        Guid documentId,
        string correlationId,
        CancellationToken cancellationToken) => GetDocumentAsync(
        accountId, documentId, correlationId, "paymentin", cancellationToken);

    public Task<string> GetCashInAsync(
        Guid accountId,
        Guid documentId,
        string correlationId,
        CancellationToken cancellationToken) => GetDocumentAsync(
        accountId, documentId, correlationId, "cashin", cancellationToken);

    public Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> PaymentInBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
        CancellationToken cancellationToken) => SendBatchAsync(
        accountId, correlationId, "paymentin", items, cancellationToken);

    public Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> CashInBatchAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
        CancellationToken cancellationToken) => SendBatchAsync(
        accountId, correlationId, "cashin", items, cancellationToken);

    private async Task<string> GetDocumentAsync(
        Guid accountId,
        Guid documentId,
        string correlationId,
        string entityType,
        CancellationToken cancellationToken)
    {
        ValidateId(documentId, nameof(documentId));
        var endpoint = $"entity/{entityType}/{documentId:D}?expand=operations";
        return (await SendJsonAsync(
            accountId,
            correlationId,
            HttpMethod.Get,
            endpoint,
            entityType,
            documentId,
            null,
            cancellationToken)).Body;
    }

    private async Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> SendBatchAsync(
        Guid accountId,
        string correlationId,
        string entityType,
        IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count is < 1 or > BatchSize ||
            items.Any(item => item.DocumentId == Guid.Empty || string.IsNullOrWhiteSpace(item.PayloadJson)) ||
            items.Select(item => item.DocumentId).Distinct().Count() != items.Count)
        {
            throw new ArgumentException(
                $"A unique {entityType} batch with 1 to {BatchSize} items is required.",
                nameof(items));
        }

        var endpoint = $"entity/{entityType}/batch";
        var payload = "[" + string.Join(',', items.Select(item => item.PayloadJson)) + "]";
        var response = await SendJsonAsync(
            accountId,
            correlationId,
            HttpMethod.Post,
            endpoint,
            entityType,
            null,
            payload,
            cancellationToken);
        return ParseBatchResponse(accountId, correlationId, endpoint, entityType, response, items);
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
            accountId,
            correlationId,
            null,
            null,
            method.Method,
            endpoint,
            entityType,
            entityId);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        if (payload is not null)
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        _logger.LogInformation(
            "Sending MoySklad purchasereturn money relation request: account_id={AccountId}, correlation_id={CorrelationId}, http_method={HttpMethod}, endpoint={Endpoint}, entity_type={EntityType}",
            accountId,
            correlationId,
            method.Method,
            endpoint,
            entityType);

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
                context,
                "MoySklad relation request timed out.",
                stopwatch.Elapsed,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context,
                "MoySklad is unavailable.",
                stopwatch.Elapsed,
                exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(
                accountId,
                MoySkladRateLimitObservationParser.Parse(response),
                cancellationToken);
            return await _responseHandler.ReadAsync(
                response,
                context,
                stopwatch.Elapsed,
                cancellationToken);
        }
    }

    private IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult> ParseBatchResponse(
        Guid accountId,
        string correlationId,
        string endpoint,
        string entityType,
        MoySkladResponseBody response,
        IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> requested)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != requested.Count)
            {
                throw new JsonException("The relation batch response does not match the request size.");
            }

            var results = new List<MoySkladPurchaseReturnMoneyRelationBatchResult>(requested.Count);
            var seen = new HashSet<Guid>();
            var index = 0;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var error = TryReadError(row);
                var documentId = TryReadId(row);
                if (documentId is null && error is not null)
                    documentId = requested[index].DocumentId;
                if (documentId is null ||
                    documentId == Guid.Empty ||
                    !requested.Any(item => item.DocumentId == documentId) ||
                    !seen.Add(documentId.Value))
                {
                    throw new JsonException(
                        "The relation batch response contains a missing or unexpected document id.");
                }

                results.Add(new MoySkladPurchaseReturnMoneyRelationBatchResult(
                    documentId.Value,
                    row.GetRawText(),
                    error?.Code,
                    error?.Message));
                index++;
            }

            if (seen.Count != requested.Count || requested.Any(item => !seen.Contains(item.DocumentId)))
                throw new JsonException("The relation batch response does not contain every requested document.");

            return results;
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                new MoySkladRequestContext(
                    accountId,
                    correlationId,
                    null,
                    null,
                    HttpMethod.Post.Method,
                    endpoint,
                    entityType,
                    null),
                response.HttpStatus,
                $"MoySklad returned invalid {entityType} relation batch data: {exception.Message}",
                response.Body,
                TimeSpan.Zero);
        }
    }

    private static Guid? TryReadId(JsonElement row) =>
        TryReadGuidProperty(row, "id") ?? TryReadGuidFromMeta(row);

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

        var lastSegment = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        return Guid.TryParse(lastSegment, out var id) ? id : null;
    }

    private static Guid? TryReadGuidProperty(JsonElement row, string propertyName) =>
        row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;

    private static (string Code, string Message)? TryReadError(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var first = errors.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return (
                "PURCHASERETURN_RELATION_BATCH_ITEM_REJECTED",
                "MoySklad rejected the relation batch item.");
        }

        var code = first.TryGetProperty("code", out var codeValue)
            ? codeValue.ToString()
            : "PURCHASERETURN_RELATION_BATCH_ITEM_REJECTED";
        var message = first.TryGetProperty("error", out var messageValue)
            ? messageValue.ToString()
            : "MoySklad rejected the relation batch item.";
        return (code, message);
    }

    private static void ValidateId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A non-empty document id is required.", parameterName);
    }
}
