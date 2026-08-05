using System.Net.Http.Json;
using System.Globalization;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Services;

public sealed record MoySkladRawResponse(
    string Json,
    int StatusCode,
    string? ContentType);

public sealed class EgressClientException(
    string code,
    string safeMessage,
    int statusCode) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
}

public interface IMoySkladEgressClient
{
    Task<MoySkladRawResponse> GetCounterpartiesAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class MoySkladEgressClient(
    HttpClient httpClient,
    IConfiguration configuration) : IMoySkladEgressClient
{
    public async Task<MoySkladRawResponse> GetCounterpartiesAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var query = $"internal/accounts/{accountId:D}/counterparties" +
                    $"?archived={archived.ToString().ToLowerInvariant()}&limit={limit}&offset={offset}";
        if (windowFrom is not null && windowTo is not null)
        {
            query += $"&windowFrom={Uri.EscapeDataString(windowFrom.Value.ToString("O", CultureInfo.InvariantCulture))}" +
                     $"&windowTo={Uri.EscapeDataString(windowTo.Value.ToString("O", CultureInfo.InvariantCulture))}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.TryAddWithoutValidation(
            InternalApiHeaders.ApiKey,
            configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.SyncRunId, syncRunId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));

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
            throw new EgressClientException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service timed out.",
                503);
        }
        catch (HttpRequestException)
        {
            throw new EgressClientException(
                "EGRESS_UNAVAILABLE",
                "MoySklad Egress Service is unavailable.",
                503);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                InternalErrorResponse? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(
                        cancellationToken: cancellationToken);
                }
                catch (Exception exception) when (
                    exception is System.Text.Json.JsonException or NotSupportedException)
                {
                    // Use the stable fallback below.
                }

                throw new EgressClientException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return new MoySkladRawResponse(
                json,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType);
        }
    }
}
