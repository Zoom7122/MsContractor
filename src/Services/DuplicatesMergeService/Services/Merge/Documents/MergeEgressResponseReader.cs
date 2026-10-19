using System.Text.Json;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Models.Exceptions;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

internal static class MergeEgressResponseReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static T Read<T>(
        MergeEgressHttpResponse response,
        string invalidResponseCode,
        string emptyResponseMessage,
        string invalidResponseMessage,
        bool unknownResultOnServerError = false)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = TryReadError(response.Body);
            var useUnknownResult = unknownResultOnServerError &&
                                   (response.StatusCode == 429 || response.StatusCode >= 500);
            throw new MergeEgressException(
                useUnknownResult
                    ? MergeEgressException.PurchaseReturnUnknownResult
                    : error?.Code ?? "EGRESS_UNAVAILABLE",
                error?.Message ?? "MoySklad Egress Service returned an error.",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(response.Body))
            throw new MergeEgressException(invalidResponseCode, emptyResponseMessage, 502);

        try
        {
            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions)
                ?? throw new MergeEgressException(invalidResponseCode, emptyResponseMessage, 502);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new MergeEgressException(invalidResponseCode, invalidResponseMessage, 502);
        }
    }

    private static InternalErrorResponse? TryReadError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<InternalErrorResponse>(body, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
