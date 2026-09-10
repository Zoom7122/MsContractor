using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.Contracts.Internal;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface IDocumentDiscoveryEgressClient
{
    Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
        Guid accountId,
        IReadOnlyList<Guid> counterpartyIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class DocumentDiscoveryEgressClient : IDocumentDiscoveryEgressClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public DocumentDiscoveryEgressClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    /// <summary>
    /// Запрос документов КА основного и дублей у Engress
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="counterpartyIds"></param>
    /// <param name="mergeJobId"></param>
    /// <param name="operationId"></param>
    /// <param name="userId"></param>
    /// <param name="correlationId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="MergeEgressException"></exception>
    public async Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
        Guid accountId,
        IReadOnlyList<Guid> counterpartyIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/discover")
        {
            Content = JsonContent.Create(new MoySkladDocumentDiscoveryRequest(counterpartyIds))
        };
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.MergeJobId, mergeJobId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.OperationId, operationId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId.ToString("D"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad document discovery timed out.", 503);
        }
        catch (HttpRequestException)
        {
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad Egress Service is unavailable.", 503);
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
                    // The Egress response is untrusted; use a stable fallback.
                }

                throw new MergeEgressException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<MoySkladDocumentDiscoveryResponse>(cancellationToken)
                    ?? throw new MergeEgressException(
                        "EGRESS_INVALID_RESPONSE",
                        "MoySklad Egress Service returned an empty document discovery response.",
                        502);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new MergeEgressException(
                    "EGRESS_INVALID_RESPONSE",
                    "MoySklad Egress Service returned an invalid document discovery response.",
                    502);
            }
        }
    }
}
