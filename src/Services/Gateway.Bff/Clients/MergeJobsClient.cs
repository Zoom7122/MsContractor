using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;

namespace MsContractor.Gateway.Bff.Clients;

public interface IMergeJobsClient
{
    Task<MergeJobAccepted> CreateAsync(
        Guid accountId,
        Guid userId,
        Guid correlationId,
        CreateMergeJobRequest request,
        CancellationToken cancellationToken);
}

public sealed class MergeJobsClient : IMergeJobsClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MergeJobsClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<MergeJobAccepted> CreateAsync(
        Guid accountId,
        Guid userId,
        Guid correlationId,
        CreateMergeJobRequest mergeRequest,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/merge-jobs")
        {
            Content = JsonContent.Create(mergeRequest)
        };
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.AccountId, accountId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId.ToString("D"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryReadErrorAsync(response, cancellationToken);
                throw new MergeJobsClientException(
                    (int)response.StatusCode,
                    error?.Code ?? "MERGE_SERVICE_UNAVAILABLE",
                    error?.Message ?? "Duplicates Merge Service rejected the request.");
            }

            return await response.Content.ReadFromJsonAsync<MergeJobAccepted>(cancellationToken)
                ?? throw Unavailable();
        }
        catch (MergeJobsClientException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable(exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(exception);
        }
    }

    private static async Task<InternalErrorResponse?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<InternalErrorResponse>(cancellationToken);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static MergeJobsClientException Unavailable(Exception? inner = null) =>
        new(503, "MERGE_SERVICE_UNAVAILABLE", "Duplicates Merge Service is unavailable.", inner);
}
