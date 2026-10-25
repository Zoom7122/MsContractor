using System.Net;
using System.Text.Json;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Models.Exceptions;

namespace MsContractor.Gateway.Bff.Clients;

public interface ISyncRunHistoryClient
{
    Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, string correlationId, CancellationToken cancellationToken);
    Task<SyncRunErrorsResponse?> GetErrorsAsync(Guid accountId, Guid syncRunId, int page, int pageSize, string correlationId, CancellationToken cancellationToken);
}

public sealed class SyncRunHistoryClient : ISyncRunHistoryClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public SyncRunHistoryClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<SyncRunHistoryResponse> GetAsync(
        Guid accountId, int page, int pageSize, string correlationId, CancellationToken cancellationToken)
        => await GetResponseAsync<SyncRunHistoryResponse>(
            $"internal/accounts/{accountId:D}/sync-runs?page={page}&pageSize={pageSize}",
            correlationId, false, cancellationToken)
           ?? throw new CatalogSyncUnavailableException("CatalogSyncService returned an empty sync history.");

    public Task<SyncRunErrorsResponse?> GetErrorsAsync(
        Guid accountId, Guid syncRunId, int page, int pageSize, string correlationId, CancellationToken cancellationToken)
        => GetResponseAsync<SyncRunErrorsResponse>(
            $"internal/accounts/{accountId:D}/sync-runs/{syncRunId:D}/errors?page={page}&pageSize={pageSize}",
            correlationId, true, cancellationToken);

    private async Task<T?> GetResponseAsync<T>(string path, string correlationId, bool allowNotFound, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
                throw new CatalogSyncUnavailableException("CatalogSyncService rejected the sync history request.");
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid sync history response.");
        }
        catch (HttpRequestException exception)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService is unavailable.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService timed out.", exception);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new CatalogSyncUnavailableException("CatalogSyncService returned an invalid sync history response.", exception);
        }
    }
}
