using MsContractor.VendorService.Models;
using MsContractor.VendorService.Services;
using MsContractor.VendorService.Models.Exceptions;
using System.Net;
using System.Net.Http.Headers;

namespace MsContractor.VendorService.Clients;

public interface IMoyskladContextClient
{
    Task<MoyskladEmployeeContext> GetAsync(
        string contextKey,
        Guid appId,
        string appUid,
        CancellationToken cancellationToken);
}

public sealed class MoyskladContextClient(
    HttpClient httpClient,
    MoyskladVendorJwtFactory jwtFactory,
    ILogger<MoyskladContextClient> logger) : IMoyskladContextClient
{
    public async Task<MoyskladEmployeeContext> GetAsync(
        string contextKey,
        Guid appId,
        string appUid,
        CancellationToken cancellationToken)
    {
        var path =
            $"context/{Uri.EscapeDataString(contextKey)}" +
            $"?appUid={Uri.EscapeDataString(appUid)}&appId={appId:D}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtFactory.Create());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VendorUpstreamException("MoySklad context request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new VendorUpstreamException("MoySklad context request failed.", exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new VendorForbiddenException();
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new VendorContextExpiredException();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "MoySklad context endpoint returned status {StatusCode}.",
                    (int)response.StatusCode);
                throw new VendorUpstreamException("MoySklad context endpoint returned an unexpected response.");
            }

            try
            {
                var context = await response.Content.ReadFromJsonAsync<MoyskladEmployeeContext>(
                    cancellationToken: cancellationToken);
                if (context is null || context.Id == Guid.Empty || context.AccountId == Guid.Empty)
                    throw new VendorUpstreamException("MoySklad context response is incomplete.");
                return context;
            }
            catch (VendorUpstreamException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is System.Text.Json.JsonException or NotSupportedException or HttpRequestException)
            {
                throw new VendorUpstreamException("MoySklad context response is invalid.", exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VendorUpstreamException("MoySklad context response timed out.");
            }
        }
    }
}
