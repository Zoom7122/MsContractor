using System.Net.Http.Json;
using System.Text.Json;
using MsContractor.Contracts.Internal;

namespace MergeVerifier.Client;

public sealed class EgressClient(HttpClient httpClient, string internalApiKey)
{
    public async Task<MoySkladMergeVerificationSnapshotResponse> CaptureAsync(Guid accountId,
        IReadOnlyList<Guid> counterpartyIds, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"internal/accounts/{accountId:D}/documents/merge-verification-snapshot")
        {
            Content = JsonContent.Create(new MoySkladMergeVerificationSnapshotRequest(counterpartyIds))
        };
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, internalApiKey);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, Guid.NewGuid().ToString("D"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = "MoySklad Egress Service returned an error.";
            try
            {
                var error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(cancellationToken);
                message = error is null ? message : $"{error.Code}: {error.Message}";
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException) { }
            throw new InvalidOperationException($"Egress capture failed ({(int)response.StatusCode}): {message}");
        }
        return await response.Content.ReadFromJsonAsync<MoySkladMergeVerificationSnapshotResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Egress returned an empty merge-verification snapshot.");
    }
}
