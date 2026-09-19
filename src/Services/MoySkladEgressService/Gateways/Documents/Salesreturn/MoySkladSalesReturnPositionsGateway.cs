using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;

public interface IMoySkladSalesReturnPositionsGateway
{
    Task<MoySkladSalesReturnPositionsPage> GetPageAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        Guid salesReturnId,
        int limit,
        int offset,
        CancellationToken cancellationToken);
}

public sealed record MoySkladSalesReturnPositionsPage(
    int Size,
    int Limit,
    int Offset,
    IReadOnlyDictionary<Guid, string> Positions);

public sealed class MoySkladSalesReturnPositionsGateway : IMoySkladSalesReturnPositionsGateway
{
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladSalesReturnPositionsGateway> _logger;

    public MoySkladSalesReturnPositionsGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladSalesReturnPositionsGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
        _logger = logger;
    }

    public const int PageSize = 1000;

    public async Task<MoySkladSalesReturnPositionsPage> GetPageAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        Guid salesReturnId,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        if (salesReturnId == Guid.Empty)
            throw new ArgumentException("A non-empty salesreturn id is required.", nameof(salesReturnId));
        if (limit != PageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"The positions limit must be {PageSize}.");
        if (offset < 0 || offset % PageSize != 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The positions offset must be a non-negative page offset.");

        var endpoint = $"entity/salesreturn/{salesReturnId:D}/positions?limit={PageSize}&offset={offset}";
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "salesreturn", salesReturnId);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad salesreturn positions request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, salesreturn_id={SalesReturnId}, limit={Limit}, offset={Offset}",
            accountId, requestedByUserId, correlationId, salesReturnId, PageSize, offset);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad salesreturn positions request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad salesreturn positions are unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);

            return ParseResponse(
                responseBody.Body, responseBody.HttpStatus, context, stopwatch.Elapsed, offset);
        }
    }

    private MoySkladSalesReturnPositionsPage ParseResponse(
        string body,
        int statusCode,
        MoySkladRequestContext context,
        TimeSpan duration,
        int requestedOffset)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("meta", out var meta) ||
                meta.ValueKind != JsonValueKind.Object ||
                !meta.TryGetProperty("size", out var sizeProperty) ||
                !meta.TryGetProperty("limit", out var limitProperty) ||
                !meta.TryGetProperty("offset", out var offsetProperty) ||
                !sizeProperty.TryGetInt32(out var size) ||
                !limitProperty.TryGetInt32(out var limit) ||
                !offsetProperty.TryGetInt32(out var offset) ||
                size < 0 || limit != PageSize || offset != requestedOffset ||
                !root.TryGetProperty("rows", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new JsonException("The salesreturn positions response has invalid pagination metadata.");

            var expectedRows = Math.Min(PageSize, size - requestedOffset);
            if (size < requestedOffset || rows.GetArrayLength() != expectedRows)
                throw new JsonException("The salesreturn positions response contains an incomplete page.");

            var result = new Dictionary<Guid, string>(rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var positionId) ||
                    positionId == Guid.Empty ||
                    !result.TryAdd(positionId, row.GetRawText()))
                    throw new JsonException("The salesreturn positions response contains a missing or duplicate position id.");
            }

            return new MoySkladSalesReturnPositionsPage(size, limit, offset, result);
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                context, statusCode,
                $"MoySklad returned an incomplete salesreturn positions response: {exception.Message}",
                body, duration);
        }
    }
}
