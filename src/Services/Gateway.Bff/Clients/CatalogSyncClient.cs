using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Sync;

namespace MsContractor.Gateway.Bff.Clients;

public interface ICatalogSyncClient
{
    Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken);
}

public interface ICatalogSyncStateClient
{
    Task<CatalogStateResponse> GetStateAsync(
        Guid accountId,
        string correlationId,
        CancellationToken cancellationToken);
}

public interface ICatalogSettingsClient
{
    Task<CatalogSettingsResponse> GetSettingsAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task SaveSettingsAsync(
        Guid accountId,
        CatalogSettingsRequest settings,
        CancellationToken cancellationToken);
}

public sealed class CatalogSyncClient : ICatalogSyncClient, ICatalogSyncStateClient, ICatalogSettingsClient
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

    public async Task<CatalogSettingsResponse> GetSettingsAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/accounts/{accountId:D}/settings");
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            _configuration["InternalApi:Key"]);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new CatalogSyncUnavailableException("CatalogSyncService rejected the settings request.");

            return await response.Content.ReadFromJsonAsync<CatalogSettingsResponse>(cancellationToken)
                ?? throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid settings response.");
        }
        catch (CatalogSyncUnavailableException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService is unavailable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService timed out.", exception);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid settings response.", exception);
        }
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

    public async Task<CatalogStateResponse> GetStateAsync(
        Guid accountId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/accounts/{accountId:D}/state");
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.CorrelationId,
            correlationId);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new CatalogSyncUnavailableException("CatalogSyncService rejected the state request.");

            return await response.Content.ReadFromJsonAsync<CatalogStateResponse>(cancellationToken)
                ?? throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid state response.");
        }
        catch (CatalogSyncUnavailableException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService is unavailable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService timed out.", exception);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid state response.", exception);
        }
    }

    public async Task SaveSettingsAsync(
        Guid accountId,
        CatalogSettingsRequest settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"internal/accounts/{accountId:D}/settings")
        {
            Content = JsonContent.Create(settings)
        };
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            _configuration["InternalApi:Key"]);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var error = await TryReadErrorAsync(response, cancellationToken);
                throw new CatalogSettingsRejectedException(
                    error?.Code ?? "INVALID_CATALOG_SETTINGS",
                    error?.Message ?? "CatalogSyncService rejected the settings.");
            }

            if (!response.IsSuccessStatusCode)
                throw new CatalogSyncUnavailableException("CatalogSyncService rejected the settings request.");
        }
        catch (CatalogSettingsRejectedException)
        {
            throw;
        }
        catch (CatalogSyncUnavailableException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService is unavailable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService timed out.", exception);
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
}
