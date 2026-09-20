using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;

public interface IMoySkladSupplyGateway
{
    Task<IReadOnlyDictionary<Guid, MoySkladSupplyReference>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> supplyIds,
        CancellationToken cancellationToken);
}

public sealed record MoySkladSupplyReference(Guid Id, Guid? AgentId);

public sealed class MoySkladSupplyGateway : IMoySkladSupplyGateway
{
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly IMoySkladResponseHandler _responseHandler;
    private readonly ILogger<MoySkladSupplyGateway> _logger;

    public MoySkladSupplyGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        IMoySkladResponseHandler responseHandler,
        ILogger<MoySkladSupplyGateway> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _responseHandler = responseHandler;
        _logger = logger;
    }

    public const int BatchSize = 1000;

    public async Task<IReadOnlyDictionary<Guid, MoySkladSupplyReference>> GetAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> supplyIds,
        CancellationToken cancellationToken)
    {
        if (supplyIds.Count == 0)
            return new Dictionary<Guid, MoySkladSupplyReference>();
        if (supplyIds.Any(id => id == Guid.Empty) || supplyIds.Distinct().Count() != supplyIds.Count)
            throw new ArgumentException("A unique non-empty supply id list is required.", nameof(supplyIds));

        var result = new Dictionary<Guid, MoySkladSupplyReference>();
        foreach (var batch in supplyIds.Chunk(BatchSize))
        {
            var rows = await GetBatchAsync(
                accountId, requestedByUserId, correlationId, batch, cancellationToken);
            foreach (var row in rows)
            {
                if (!result.TryAdd(row.Key, row.Value))
                    throw new InvalidOperationException($"MoySklad returned duplicate supply {row.Key:D}.");
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, MoySkladSupplyReference>> GetBatchAsync(
        Guid accountId,
        Guid requestedByUserId,
        string correlationId,
        IReadOnlyList<Guid> supplyIds,
        CancellationToken cancellationToken)
    {
        var filter = string.Join(';', supplyIds.Select(id => $"id={id:D}"));
        var endpoint = $"entity/supply?filter={Uri.EscapeDataString(filter)}&limit={BatchSize}";
        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, HttpMethod.Get.Method, endpoint, "supply", null);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        _logger.LogInformation(
            "Sending MoySklad supply request: account_id={AccountId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, supply_count={SupplyCount}, limit={Limit}",
            accountId, requestedByUserId, correlationId, supplyIds.Count, BatchSize);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad supply request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad supply is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            var observation = MoySkladRateLimitObservationParser.Parse(response);
            await _rateLimiter.ObserveAsync(accountId, observation, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            return ParseResponse(responseBody.Body, responseBody.HttpStatus, context, stopwatch.Elapsed, supplyIds);
        }
    }

    private IReadOnlyDictionary<Guid, MoySkladSupplyReference> ParseResponse(
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
                throw new JsonException("The supply response does not contain rows.");

            var requested = requestedIds.ToHashSet();
            var result = new Dictionary<Guid, MoySkladSupplyReference>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(idProperty.GetString(), out var id) ||
                    id == Guid.Empty ||
                    !requested.Contains(id))
                    throw new JsonException("The supply response contains an invalid or unexpected document.");

                Guid? agentId = null;
                if (row.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object &&
                    agent.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                    meta.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String)
                    agentId = TryReadEntityId(href.GetString());

                if (!result.TryAdd(id, new MoySkladSupplyReference(id, agentId)))
                    throw new JsonException("The supply response contains a duplicate document.");
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw _responseHandler.ValidationFailure(
                context,
                statusCode,
                $"MoySklad returned invalid supply data: {exception.Message}",
                body,
                duration);
        }
    }

    private static Guid? TryReadEntityId(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return null;
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 3 &&
               string.Equals(segments[^3], "entity", StringComparison.Ordinal) &&
               Guid.TryParse(segments[^1], out var id) && id != Guid.Empty
            ? id
            : null;
    }
}
