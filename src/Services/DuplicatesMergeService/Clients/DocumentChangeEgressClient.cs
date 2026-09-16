using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.Contracts.Internal;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface IDocumentChangeEgressClient
{
    Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class DocumentChangeEgressClient : IDocumentChangeEgressClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public DocumentChangeEgressClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        return await SendAsync<MoySkladDocumentChangeCounterpartyRequest, MoySkladDocumentChangeCounterpartyResponse>(
            accountId,
            "change-counterparty",
            new MoySkladDocumentChangeCounterpartyRequest(mainCounterpartyId, documents),
            mergeJobId,
            operationId,
            userId,
            correlationId,
            cancellationToken);
    }

    public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<MoySkladDocumentChangeAgentAndContractRequest, MoySkladDocumentChangeCounterpartyResponse>(
            accountId,
            "change-agent-and-contract",
            new MoySkladDocumentChangeAgentAndContractRequest(mainCounterpartyId, documents),
            mergeJobId,
            operationId,
            userId,
            correlationId,
            cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        Guid accountId,
        string operation,
        TRequest payload,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken) where TResponse : class
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/{operation}")
        {
            Content = JsonContent.Create(payload)
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
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad document change timed out.", 503);
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
                    // Use a stable fallback for an untrusted Egress response.
                }
                throw new MergeEgressException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
                    ?? throw new MergeEgressException(
                        "EGRESS_INVALID_RESPONSE",
                        "MoySklad Egress Service returned an empty document change response.",
                        502);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new MergeEgressException(
                    "EGRESS_INVALID_RESPONSE",
                    "MoySklad Egress Service returned an invalid document change response.",
                    502);
            }
        }
    }
}
