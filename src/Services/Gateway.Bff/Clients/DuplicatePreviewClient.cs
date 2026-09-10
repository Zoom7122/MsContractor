using MsContractor.Gateway.Bff.Models.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using MsContractor.Contracts.Duplicates;
using MsContractor.Contracts.Internal;

namespace MsContractor.Gateway.Bff.Clients;

public interface IDuplicatePreviewClient
{
    Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(
        Guid accountId,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken);
}

public sealed class DuplicatePreviewClient : IDuplicatePreviewClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public DuplicatePreviewClient(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(
        Guid accountId,
        IReadOnlyList<string> fields,
        CancellationToken cancellationToken)
    {
        var query = fields.Select(field => new KeyValuePair<string, string?>("fields", field));
        using var request = new HttpRequestMessage(HttpMethod.Post, QueryHelpers.AddQueryString("internal/merge-preview", query));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, _configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.AccountId, accountId.ToString("D"));
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                throw new DuplicatePreviewValidationException();
            if (!response.IsSuccessStatusCode)
                throw new DuplicatePreviewUnavailableException("DuplicatesMergeService rejected the request.");
            return await response.Content.ReadFromJsonAsync<List<DuplicateGroupDto>>(cancellationToken)
                ?? throw new DuplicatePreviewUnavailableException("DuplicatesMergeService returned an invalid response.");
        }
        catch (HttpRequestException exception)
        {
            throw new DuplicatePreviewUnavailableException("DuplicatesMergeService is unavailable.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DuplicatePreviewUnavailableException("DuplicatesMergeService timed out.", exception);
        }
    }
}
