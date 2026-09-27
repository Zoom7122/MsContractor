using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface IMergeEgressClient
{
    Task<MergeEgressResponse> UpdateAsync(
        Guid accountId, Guid counterpartyId, MergeMainCounterpartyDto update,
        Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
        CancellationToken cancellationToken);

    Task<MergeEgressResponse> ArchiveAsync(
        Guid accountId, IReadOnlyList<Guid> counterpartyIds, Guid mergeJobId,
        Guid userId, Guid correlationId, CancellationToken cancellationToken);
}

public sealed class MergeEgressClient : IMergeEgressClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MergeEgressClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }


    /// <summary>
    /// Обновление основного КА зарос в engress
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="counterpartyId"></param>
    /// <param name="update"></param>
    /// <param name="mergeJobId"></param>
    /// <param name="operationId"></param>
    /// <param name="userId"></param>
    /// <param name="correlationId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<MergeEgressResponse> UpdateAsync(
        Guid accountId, Guid counterpartyId, MergeMainCounterpartyDto update,
        Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
        CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Put,
            $"internal/accounts/{accountId:D}/counterparties/{counterpartyId:D}",
            new InternalCounterpartyUpdateRequest(
                update.Name,
                update.Email,
                update.Phone,
                update.Description,
                update.Attributes?.Select(attribute => new InternalCounterpartyAttributeUpdate(
                    attribute.Id, attribute.Type, attribute.Value, attribute.File)).ToArray()),
            mergeJobId, operationId, userId, correlationId, cancellationToken);

/// <summary>
/// Запрос в engress на архивацию контрагентов
/// </summary>
/// <param name="accountId"></param>
/// <param name="counterpartyIds"></param>
/// <param name="mergeJobId"></param>
/// <param name="userId"></param>
/// <param name="correlationId"></param>
/// <param name="cancellationToken"></param>
/// <returns></returns>
    public Task<MergeEgressResponse> ArchiveAsync(
        Guid accountId, IReadOnlyList<Guid> counterpartyIds, Guid mergeJobId,
        Guid userId, Guid correlationId, CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Put,
            $"internal/accounts/{accountId:D}/counterparties/archive",
            new InternalCounterpartyBatchArchiveRequest(counterpartyIds),
            mergeJobId, null, userId, correlationId, cancellationToken);

    private async Task<MergeEgressResponse> SendAsync<T>(
        HttpMethod method,
        string uri,
        T? body,
        Guid mergeJobId,
        Guid? operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.MergeJobId, mergeJobId.ToString("D"));
        if (operationId is not null)
            request.Headers.TryAddWithoutValidation(InternalApiHeaders.OperationId, operationId.Value.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId.ToString("D"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad Egress Service timed out.", 503);
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
                    // Use a stable fallback without copying the upstream response.
                }

                throw new MergeEgressException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "MoySklad Egress Service returned an empty response.", 502);
            return new MergeEgressResponse(json);
        }
    }
}
