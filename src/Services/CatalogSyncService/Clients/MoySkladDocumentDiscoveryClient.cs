using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Clients;

public interface IMoySkladDocumentDiscoveryClient
{
    Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
        Guid accountId,
        IReadOnlyList<Guid> counterpartyIds,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class MoySkladDocumentDiscoveryClient : IMoySkladDocumentDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MoySkladDocumentDiscoveryClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
        Guid accountId,
        IReadOnlyList<Guid> counterpartyIds,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/discover")
        {
            Content = JsonContent.Create(new MoySkladDocumentDiscoveryRequest(counterpartyIds))
        };
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EgressClientException("EGRESS_UNAVAILABLE", "MoySklad document discovery timed out.", 503);
        }
        catch (HttpRequestException)
        {
            throw new EgressClientException("EGRESS_UNAVAILABLE", "MoySklad Egress Service is unavailable.", 503);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                InternalErrorResponse? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(cancellationToken);
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
                {
                    // Use the stable fallback below for untrusted error payloads.
                }

                throw new EgressClientException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<MoySkladDocumentDiscoveryResponse>(cancellationToken)
                    ?? throw new EgressClientException(
                        "EGRESS_INVALID_RESPONSE",
                        "MoySklad Egress Service returned an empty document discovery response.",
                        502);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new EgressClientException(
                    "EGRESS_INVALID_RESPONSE",
                    "MoySklad Egress Service returned an invalid document discovery response.",
                    502);
            }
        }
    }
}
