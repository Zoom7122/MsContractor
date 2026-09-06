using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;

namespace MsContractor.MoySkladEgressService.Gateways;

public sealed partial class MoySkladDocumentGateway
{
    private async Task<string> CompleteSalesReturnPositionsAsync(Guid accountId, Guid userId, string correlationId,
        Guid documentId, string rawJson, string accessToken, CancellationToken cancellationToken)
    {
        var document = JsonNode.Parse(rawJson)!.AsObject();
        if (document["positions"] is JsonArray) return rawJson;
        var positions = new JsonArray();
        var seen = new HashSet<Guid>();
        int? expectedSize = null;
        for (var offset = 0; expectedSize is null || offset < expectedSize; offset += 1000)
        {
            var path = $"entity/salesreturn/{documentId:D}/positions?limit=1000&offset={offset}";
            var context = new MoySkladRequestContext(accountId, correlationId, null, null, "GET", path, "salesreturn", documentId);
            var watch = Stopwatch.StartNew();
            await rateLimiter.WaitAsync(accountId, userId, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            try
            {
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await rateLimiter.ObserveAsync(accountId, response, cancellationToken);
                var body = await responseHandler.ReadAsync(response, context, watch.Elapsed, cancellationToken);
                var page = JsonNode.Parse(body.Body)?.AsObject() ?? throw new JsonException();
                var size = page["meta"]?["size"]?.GetValue<int>() ?? throw new JsonException();
                if (size < 0 || page["meta"]?["limit"]?.GetValue<int>() != 1000 ||
                    page["meta"]?["offset"]?.GetValue<int>() != offset || expectedSize is { } expected && expected != size ||
                    page["rows"] is not JsonArray rows || rows.Count != Math.Min(1000, size - offset))
                    throw new JsonException("Incomplete salesreturn positions pagination.");
                expectedSize = size;
                foreach (var row in rows)
                {
                    if (row is not JsonObject position || !Guid.TryParse(position["id"]?.ToString(), out var id) || id == Guid.Empty || !seen.Add(id))
                        throw new JsonException("Missing or duplicate salesreturn position id.");
                    positions.Add(position.DeepClone());
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            { throw responseHandler.TransportFailure(context, "Salesreturn positions request timed out.", watch.Elapsed, exception); }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            { throw responseHandler.TransportFailure(context, "Salesreturn positions are unavailable.", watch.Elapsed, exception); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            { throw new EgressException(502, "SALESRETURN_POSITIONS_INCOMPLETE", "MoySklad returned incomplete salesreturn positions.", exception); }
        }
        document["positions"] = positions;
        return document.ToJsonString();
    }
}
