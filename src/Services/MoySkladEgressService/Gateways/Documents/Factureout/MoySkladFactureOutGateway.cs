using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;

public interface IMoySkladFactureOutGateway
{
    Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        Guid accountId,
        string correlationId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken);
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

    private async Task<MoySkladResponseBody> SendGetAsync(
        Guid accountId,
        string correlationId,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "factureout", null);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

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
