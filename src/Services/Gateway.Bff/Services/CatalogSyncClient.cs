using System.Net;
using System.Net.Http.Json;
using MsContractor.Contracts.Sync;

namespace MsContractor.Gateway.Bff.Services;

public interface ICatalogSyncClient
{
    Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken);
}

public sealed class CatalogSyncClient(
    HttpClient httpClient,
    IConfiguration configuration) : ICatalogSyncClient
{
    public async Task<SyncAccepted> StartAsync(
        SyncStartRequest syncRequest,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/sync")
        {
            Content = JsonContent.Create(syncRequest)
        };
        request.Headers.TryAddWithoutValidation(
            "X-Internal-Api-Key",
            configuration["InternalApi:Key"]);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService is unavailable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService timed out.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CatalogSyncUnavailableException("CatalogSyncService rejected the synchronization request.");

            var accepted = await response.Content.ReadFromJsonAsync<SyncAccepted>(cancellationToken);
            return accepted ?? throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid response.");
        }
    }
}

public sealed class CatalogSyncUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
