using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.Contracts.Internal;

namespace MsContractor.DuplicatesMergeService.Clients;

public interface IDocumentChangeEgressClient
{
    Task<MergeEgressHttpResponse> ChangeCounterpartyAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);

    Task<MergeEgressHttpResponse> ChangeAgentAndContractAsync(
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

    public Task<MergeEgressHttpResponse> ChangeCounterpartyAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            accountId,
            "change-counterparty",
            new MoySkladDocumentChangeCounterpartyRequest(mainCounterpartyId, documents),
            mergeJobId,
            operationId,
            userId,
            correlationId,
            cancellationToken);
    }

    public Task<MergeEgressHttpResponse> ChangeAgentAndContractAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken) =>
        SendAsync(
            accountId,
            "change-agent-and-contract",
            new MoySkladDocumentChangeAgentAndContractRequest(mainCounterpartyId, documents),
            mergeJobId,
            operationId,
            userId,
            correlationId,
            cancellationToken);

    private async Task<MergeEgressHttpResponse> SendAsync<TRequest>(
        Guid accountId,
        string operation,
        TRequest payload,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
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
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new MergeEgressHttpResponse((int)response.StatusCode, body);
        }
    }
}
