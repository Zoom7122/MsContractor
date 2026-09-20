using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents;

/// <summary>
/// Read-only raw gateway used only by the internal MergeVerifier endpoint. Keeping it
/// in Egress guarantees that every upstream call uses the established token and limiter.
/// </summary>
public interface IMoySkladMergeVerificationGateway
{
    Task<string> GetCounterpartyAsync(Guid accountId, Guid counterpartyId, string correlationId,
        CancellationToken cancellationToken);
    Task<MoySkladMergeVerificationPage> GetDocumentPageAsync(Guid accountId, string documentType,
        IReadOnlyList<Guid> counterpartyIds, int limit, int offset, string correlationId,
        CancellationToken cancellationToken);
    Task<string> GetDocumentAsync(Guid accountId, string documentType, Guid documentId, string correlationId,
        CancellationToken cancellationToken);
    Task<MoySkladMergeVerificationPage> GetPositionsPageAsync(Guid accountId, string documentType, Guid documentId,
        int limit, int offset, string correlationId, CancellationToken cancellationToken);
}

public sealed record MoySkladMergeVerificationPage(int Size, int Limit, int Offset, IReadOnlyList<string> Rows);

public sealed class MoySkladMergeVerificationGateway(
    HttpClient httpClient,
    IVendorTokenClient tokenClient,
    IMoySkladRateLimiter rateLimiter,
    IMoySkladResponseHandler responseHandler) : IMoySkladMergeVerificationGateway
{
    public Task<string> GetCounterpartyAsync(Guid accountId, Guid counterpartyId, string correlationId,
        CancellationToken cancellationToken) =>
        GetJsonAsync(accountId, $"entity/counterparty/{counterpartyId:D}", "counterparty", counterpartyId,
            correlationId, cancellationToken);

    public async Task<MoySkladMergeVerificationPage> GetDocumentPageAsync(Guid accountId, string documentType,
        IReadOnlyList<Guid> counterpartyIds, int limit, int offset, string correlationId,
        CancellationToken cancellationToken)
    {
        ValidateDocumentType(documentType);
        var filter = string.Join(';', counterpartyIds.Select(id =>
            $"agent={new Uri(httpClient.BaseAddress!, $"entity/counterparty/{id:D}")}"));
        var json = await GetJsonAsync(accountId,
            $"entity/{documentType}?filter={Uri.EscapeDataString(filter)}&limit={limit}&offset={offset}",
            documentType, null, correlationId, cancellationToken);
        return ParsePage(json, "document");
    }

    public Task<string> GetDocumentAsync(Guid accountId, string documentType, Guid documentId, string correlationId,
        CancellationToken cancellationToken)
    {
        ValidateDocumentType(documentType);
        return GetJsonAsync(accountId, $"entity/{documentType}/{documentId:D}", documentType, documentId,
            correlationId, cancellationToken);
    }

    public async Task<MoySkladMergeVerificationPage> GetPositionsPageAsync(Guid accountId, string documentType,
        Guid documentId, int limit, int offset, string correlationId, CancellationToken cancellationToken)
    {
        ValidateDocumentType(documentType);
        var json = await GetJsonAsync(accountId,
            $"entity/{documentType}/{documentId:D}/positions?limit={limit}&offset={offset}", documentType,
            documentId, correlationId, cancellationToken);
        return ParsePage(json, "position");
    }

    private async Task<string> GetJsonAsync(Guid accountId, string endpoint, string entityType, Guid? entityId,
        string correlationId, CancellationToken cancellationToken)
    {
        var context = new MoySkladRequestContext(accountId, correlationId, null, null, HttpMethod.Get.Method,
            endpoint, entityType, entityId);
        var stopwatch = Stopwatch.StartNew();
        var accessToken = await tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await rateLimiter.WaitAsync(accountId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw responseHandler.TransportFailure(context, "MoySklad request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw responseHandler.TransportFailure(context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await rateLimiter.ObserveAsync(accountId, MoySkladRateLimitObservationParser.Parse(response), cancellationToken);
            return (await responseHandler.ReadAsync(response, context, stopwatch.Elapsed, cancellationToken)).Body;
        }
    }

    private static MoySkladMergeVerificationPage ParsePage(string json, string kind)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var meta = root.GetProperty("meta");
            var size = meta.GetProperty("size").GetInt32();
            var limit = meta.GetProperty("limit").GetInt32();
            var offset = meta.GetProperty("offset").GetInt32();
            var rows = root.GetProperty("rows").EnumerateArray().Select(row => row.GetRawText()).ToArray();
            return new MoySkladMergeVerificationPage(size, limit, offset, rows);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new EgressException(502, "MOYSKLAD_RESPONSE_VALIDATION_FAILED",
                $"MoySklad returned an invalid {kind} page.", exception);
        }
    }

    private static void ValidateDocumentType(string documentType)
    {
        if (!SupportedMoySkladDocumentTypes.Contains(documentType))
            throw new ArgumentOutOfRangeException(nameof(documentType));
    }
}
