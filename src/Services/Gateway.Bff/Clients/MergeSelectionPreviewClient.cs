using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;

namespace MsContractor.Gateway.Bff.Clients;

public interface IMergeSelectionPreviewClient
{
    Task<MergeSelectionPreviewResponse> GetAsync(
        Guid accountId,
        MergeSelectionPreviewRequest request,
        CancellationToken cancellationToken);
}

public sealed class MergeSelectionPreviewClient(HttpClient httpClient, IConfiguration configuration)
    : IMergeSelectionPreviewClient
{
    public async Task<MergeSelectionPreviewResponse> GetAsync(
        Guid accountId,
        MergeSelectionPreviewRequest previewRequest,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/merge-preview/selection")
        {
            Content = JsonContent.Create(previewRequest)
        };
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.AccountId, accountId.ToString("D"));

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await TryReadErrorAsync(response, cancellationToken);
                throw new MergeSelectionPreviewClientException(
                    (int)response.StatusCode,
                    error?.Code ?? "MERGE_PREVIEW_UNAVAILABLE",
                    error?.Message ?? "Merge preview service rejected the request.");
            }

            return await response.Content.ReadFromJsonAsync<MergeSelectionPreviewResponse>(cancellationToken)
                ?? throw Unavailable();
        }
        catch (MergeSelectionPreviewClientException)
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

    private static MergeSelectionPreviewClientException Unavailable(Exception? inner = null) =>
        new(503, "MERGE_PREVIEW_UNAVAILABLE", "Merge preview service is unavailable.", inner);
}
