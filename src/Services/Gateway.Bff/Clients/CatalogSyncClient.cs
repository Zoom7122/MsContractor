using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Contracts.Sync;

namespace MsContractor.Gateway.Bff.Clients;

public interface ICatalogSyncClient
{
    Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken);
}

public sealed class CatalogSyncClient : ICatalogSyncClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public CatalogSyncClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

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
            _configuration["InternalApi:Key"]);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
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
