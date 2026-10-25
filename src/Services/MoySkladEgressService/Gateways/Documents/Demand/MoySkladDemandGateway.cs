using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Demand;

public interface IMoySkladDemandGateway
{
    Task<IReadOnlyDictionary<Guid, MoySkladDemandReference>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> demandIds,
        CancellationToken cancellationToken);
}

public sealed record MoySkladDemandReference(Guid Id, Guid? AgentId);

public sealed class MoySkladDemandGateway : IMoySkladDemandGateway
{
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladDemandGateway> _logger;

    public MoySkladDemandGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladDemandGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
        _logger = logger;
    }

    public const int BatchSize = 40;

    public async Task<IReadOnlyDictionary<Guid, MoySkladDemandReference>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> demandIds,
        CancellationToken cancellationToken)
    {
        if (demandIds.Count == 0)
            return new Dictionary<Guid, MoySkladDemandReference>();
        if (demandIds.Any(id => id == Guid.Empty) || demandIds.Distinct().Count() != demandIds.Count)
            throw new ArgumentException("A unique non-empty demand id list is required.", nameof(demandIds));

        var result = new Dictionary<Guid, MoySkladDemandReference>();
        foreach (var batch in demandIds.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await GetBatchAsync(accountId, requestedByUserId, correlationId, batch, cancellationToken);
            foreach (var row in rows)
            {
                if (!result.TryAdd(row.Key, row.Value))
                    throw new InvalidOperationException($"MoySklad returned duplicate demand {row.Key:D}.");
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, MoySkladDemandReference>> GetBatchAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> demandIds,
        CancellationToken cancellationToken)
    {
        var filter = string.Join(';', demandIds.Select(id => $"id={id:D}"));
        var endpoint = $"entity/demand?filter={Uri.EscapeDataString(filter)}&limit={BatchSize}";
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "demand", null);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad demand request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, demand_count={DemandCount}, limit={Limit}",
            accountId, requestedByUserId, correlationId, demandIds.Count, BatchSize);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad demand request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad demand is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            return ParseResponse(responseBody.Body, responseBody.HttpStatus, context, stopwatch.Elapsed, demandIds);
        }
    }

    private IReadOnlyDictionary<Guid, MoySkladDemandReference> ParseResponse(
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
                throw new JsonException("The demand response does not contain rows.");

            var requested = requestedIds.ToHashSet();
            var result = new Dictionary<Guid, MoySkladDemandReference>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var id) ||
                    id == Guid.Empty ||
                    !requested.Contains(id))
                    throw new JsonException("The demand response contains an invalid or unexpected document.");

                var agentId = TryReadCounterpartyId(row);
                if (!result.TryAdd(id, new MoySkladDemandReference(id, agentId)))
                    throw new JsonException("The demand response contains a duplicate document.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                context,
                statusCode,
                $"MoySklad returned invalid demand data: {exception.Message}",
                body,
                duration);
        }
    }

    private static Guid? TryReadCounterpartyId(JsonElement demand)
    {
        if (!demand.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object ||
            !agent.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "counterparty", StringComparison.OrdinalIgnoreCase) ||
            !meta.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Split(
            '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 3 &&
               string.Equals(segments[^3], "entity", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[^2], "counterparty", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[^1], out var id) && id != Guid.Empty
            ? id
            : null;
    }
}
