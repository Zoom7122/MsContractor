using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Models;
using System.Globalization;
using System.Text.Json;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Clients;

public interface IMoySkladEgressClient
{
    Task<MoySkladConnectionCheckResponse> CheckConnectionAsync(
        Guid accountId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<MoySkladRawResponse> GetCounterpartiesAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class MoySkladEgressClient : IMoySkladEgressClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MoySkladEgressClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<MoySkladConnectionCheckResponse> CheckConnectionAsync(
        Guid accountId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/accounts/{accountId:D}/connection");
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressClientException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service timed out.",
                503);
        }
        catch (HttpRequestException)
        {
            throw new EgressClientException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service is unavailable.",
                503);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                InternalErrorResponse? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(
                        cancellationToken: cancellationToken);
                }
                catch (Exception exception) when (
                    exception is JsonException or NotSupportedException)
                {
                    // Use the stable fallback below.
                }

                throw new EgressClientException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            try
            {
                var result = await response.Content.ReadFromJsonAsync<MoySkladConnectionCheckResponse>(
                    cancellationToken: cancellationToken);
                return result ?? throw InvalidConnectionResponse();
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException)
            {
                throw InvalidConnectionResponse();
            }
        }
    }

    public async Task<MoySkladRawResponse> GetCounterpartiesAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var query = $"internal/accounts/{accountId:D}/counterparties" +
                    $"?archived={archived.ToString().ToLowerInvariant()}&limit={limit}&offset={offset}";
        if (windowFrom is not null && windowTo is not null)
        {
            query += $"&windowFrom={Uri.EscapeDataString(windowFrom.Value.ToString("O", CultureInfo.InvariantCulture))}" +
                     $"&windowTo={Uri.EscapeDataString(windowTo.Value.ToString("O", CultureInfo.InvariantCulture))}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.SyncRunId, syncRunId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressClientException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service timed out.",
                503);
        }
        catch (HttpRequestException)
        {
            throw new EgressClientException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service is unavailable.",
                503);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                InternalErrorResponse? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(
                        cancellationToken: cancellationToken);
                }
                catch (Exception exception) when (
                    exception is System.Text.Json.JsonException or NotSupportedException)
                {
                    // Use the stable fallback below.
                }

                throw new EgressClientException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return new MoySkladRawResponse(
                json,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType);
        }
    }

    private static EgressClientException InvalidConnectionResponse() =>
        new(
            "EGRESS_INVALID_RESPONSE",
            "MoySklad Egress Service returned an invalid connection status.",
            502);
}
