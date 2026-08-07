using System.Net.Http.Json;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public sealed record MergeEgressResponse(string Json);

public sealed class MergeEgressException(
    string code,
    string safeMessage,
    int statusCode) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
    public bool IsRetryable => StatusCode == 429 || StatusCode >= 500;
}

public interface IMergeEgressClient
{
    Task<MergeEgressResponse> UpdateAsync(
        Guid accountId, Guid counterpartyId, MergeMainCounterpartyDto update,
        Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
        CancellationToken cancellationToken);

    Task<MergeEgressResponse> ArchiveAsync(
        Guid accountId, Guid counterpartyId, Guid mergeJobId, Guid operationId,
        Guid userId, Guid correlationId, CancellationToken cancellationToken);
}

public sealed class MergeEgressClient(HttpClient httpClient, IConfiguration configuration) : IMergeEgressClient
{
    public Task<MergeEgressResponse> UpdateAsync(
        Guid accountId, Guid counterpartyId, MergeMainCounterpartyDto update,
        Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
        CancellationToken cancellationToken) => SendAsync(
            HttpMethod.Put,
            $"internal/accounts/{accountId:D}/counterparties/{counterpartyId:D}",
            new InternalCounterpartyUpdateRequest(update.Name, update.Email, update.Phone, update.Description),
            mergeJobId, operationId, userId, correlationId, cancellationToken);

    public Task<MergeEgressResponse> ArchiveAsync(
        Guid accountId, Guid counterpartyId, Guid mergeJobId, Guid operationId,
        Guid userId, Guid correlationId, CancellationToken cancellationToken) => SendAsync<object>(
            HttpMethod.Put,
            $"internal/accounts/{accountId:D}/counterparties/{counterpartyId:D}/archive",
            null,
            mergeJobId, operationId, userId, correlationId, cancellationToken);

    private async Task<MergeEgressResponse> SendAsync<T>(
        HttpMethod method,
        string uri,
        T? body,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.ApiKey, configuration["InternalApi:Key"]);
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.MergeJobId, mergeJobId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.OperationId, operationId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.UserId, userId.ToString("D"));
        request.Headers.TryAddWithoutValidation(InternalApiHeaders.CorrelationId, correlationId.ToString("D"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad Egress Service timed out.", 503);
        }
        catch (HttpRequestException)
        {
            throw new MergeEgressException("EGRESS_UNAVAILABLE", "MoySklad Egress Service is unavailable.", 503);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                InternalErrorResponse? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<InternalErrorResponse>(cancellationToken);
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
                {
                    // Use a stable fallback without copying the upstream response.
                }

                throw new MergeEgressException(
                    error?.Code ?? "EGRESS_UNAVAILABLE",
                    error?.Message ?? "MoySklad Egress Service returned an error.",
                    (int)response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "MoySklad Egress Service returned an empty response.", 502);
            return new MergeEgressResponse(json);
        }
    }
}
