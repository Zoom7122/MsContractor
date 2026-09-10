using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;
using System.Net;
using Microsoft.Extensions.Options;
using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Clients;

public interface IVendorTokenClient
{
    Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class VendorTokenClient : IVendorTokenClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<EgressOptions> _options;

    public VendorTokenClient(
        HttpClient httpClient,
        IOptions<EgressOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<string> GetAccessTokenAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"internal/vendor/installations/{accountId:D}/token");
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            _options.Value.InternalApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new EgressException(
                StatusCodes.Status503ServiceUnavailable,
                "ACCESS_TOKEN_UNAVAILABLE",
                "VendorService is unavailable.",
                exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new EgressException(404, "INSTALLATION_NOT_FOUND", "Installation was not found.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new EgressException(403, "INSTALLATION_INACTIVE", "Installation is inactive.");
            if (!response.IsSuccessStatusCode)
            {
                throw new EgressException(
                    StatusCodes.Status503ServiceUnavailable,
                    "ACCESS_TOKEN_UNAVAILABLE",
                    "Access token is unavailable.");
            }

            try
            {
                var payload = await response.Content.ReadFromJsonAsync<InternalAccessTokenResponse>(
                    cancellationToken: cancellationToken);
                if (string.IsNullOrWhiteSpace(payload?.AccessToken))
                {
                    throw new EgressException(
                        StatusCodes.Status503ServiceUnavailable,
                        "ACCESS_TOKEN_UNAVAILABLE",
                        "Access token is unavailable.");
                }

                return payload.AccessToken;
            }
            catch (EgressException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is System.Text.Json.JsonException or NotSupportedException)
            {
                throw new EgressException(
                    StatusCodes.Status503ServiceUnavailable,
                    "ACCESS_TOKEN_UNAVAILABLE",
                    "VendorService returned an invalid token response.",
                    exception);
            }
        }
    }
}
