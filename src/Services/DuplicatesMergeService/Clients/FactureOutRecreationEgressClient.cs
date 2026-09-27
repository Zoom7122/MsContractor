using System.Net.Http.Json;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Models.Exceptions;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface IFactureOutRecreationEgressClient
{
    Task<FactureOutRecreationResponse> RecreateAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class FactureOutRecreationEgressClient(
    HttpClient httpClient,
    IConfiguration configuration) : IFactureOutRecreationEgressClient
{
    public async Task<FactureOutRecreationResponse> RecreateAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || mainCounterpartyId == Guid.Empty || factureOutIds.Count == 0 ||
            factureOutIds.Any(id => id == Guid.Empty) ||
            factureOutIds.Distinct().Count() != factureOutIds.Count)
        {
            throw new ArgumentException("A non-empty account and main counterparty with unique factureout ids are required.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/factureout/recreate")
        {
            Content = JsonContent.Create(new FactureOutRecreationRequest(mainCounterpartyId, factureOutIds))
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
            if (!response.IsSuccessStatusCode)
            {
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

            try
            {
                return await response.Content.ReadFromJsonAsync<FactureOutRecreationResponse>(cancellationToken)
                    ?? throw new MergeEgressException(
                        "EGRESS_INVALID_RESPONSE",
                        "MoySklad Egress Service returned an empty factureout recreation response.",
                        502);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new MergeEgressException(
                    "EGRESS_INVALID_RESPONSE",
                    "MoySklad Egress Service returned an invalid factureout recreation response.",
                    502);
            }
        }
    }
}
