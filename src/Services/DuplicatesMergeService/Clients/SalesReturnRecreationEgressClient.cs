using System.Net.Http.Json;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Models.Exceptions;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface ISalesReturnRecreationEgressClient
{
    Task<MergeEgressHttpResponse> RecreateAsync(
        Guid accountId,
        Guid mainAgentId,
        IReadOnlyList<Guid> salesReturnIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnRecreationEgressClient : ISalesReturnRecreationEgressClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public SalesReturnRecreationEgressClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<MergeEgressHttpResponse> RecreateAsync(
        Guid accountId,
        Guid mainAgentId,
        IReadOnlyList<Guid> salesReturnIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || mainAgentId == Guid.Empty || salesReturnIds.Count == 0 ||
            salesReturnIds.Any(id => id == Guid.Empty) ||
            salesReturnIds.Distinct().Count() != salesReturnIds.Count)
            throw new ArgumentException("A non-empty main agent and unique salesreturn ids are required.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/salesreturn/recreate")
        {
            Content = JsonContent.Create(new SalesReturnRecreationRequest(mainAgentId, salesReturnIds))
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
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad Egress Service timed out.", 503);
        }
        catch (HttpRequestException)
        {
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad Egress Service is unavailable.", 503);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new MergeEgressHttpResponse((int)response.StatusCode, body);
        }
    }
}
