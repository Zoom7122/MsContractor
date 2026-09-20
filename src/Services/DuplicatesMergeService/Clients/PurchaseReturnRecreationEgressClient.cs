using System.Net.Http.Json;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Models.Exceptions;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface IPurchaseReturnRecreationEgressClient
{
    Task RecreateAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnRecreationEgressClient(
    HttpClient httpClient,
    IConfiguration configuration) : IPurchaseReturnRecreationEgressClient
{
    public async Task RecreateAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || mainCounterpartyId == Guid.Empty || purchaseReturnIds.Count == 0 ||
            purchaseReturnIds.Any(id => id == Guid.Empty) ||
            purchaseReturnIds.Distinct().Count() != purchaseReturnIds.Count)
        {
            throw new ArgumentException("A non-empty main counterparty and unique purchasereturn ids are required.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/purchasereturn/recreate")
        {
            Content = JsonContent.Create(new PurchaseReturnRecreationRequest(mainCounterpartyId, purchaseReturnIds))
        };
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.MergeJobId, mergeJobId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.OperationId, operationId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId.ToString("D"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MergeEgressException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service timed out.",
                503);
        }
        catch (HttpRequestException)
        {
            throw new MergeEgressException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service is unavailable.",
                503);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            InternalErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(cancellationToken);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
            {
                // Use a stable fallback for an untrusted Egress response.
            }

            throw new MergeEgressException(
                error?.Code ?? "EGRESS_UNAVAILABLE",
                error?.Message ?? "MoySklad Egress Service returned an error.",
                (int)response.StatusCode);
        }
    }
}
