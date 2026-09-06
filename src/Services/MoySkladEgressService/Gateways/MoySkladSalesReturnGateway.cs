using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Contracts;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;

namespace MsContractor.MoySkladEgressService.Gateways;

public interface IMoySkladSalesReturnGateway
{
    Task ValidateAsync(SalesReturnCallContext context, Guid mainId, RecreateSalesReturnItem item, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(SalesReturnCallContext context, Guid oldId, CancellationToken cancellationToken);
    Task DeleteAsync(SalesReturnCallContext context, Guid oldId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SalesReturnCreated>> CreateAsync(SalesReturnCallContext context, Guid mainId,
        IReadOnlyList<SalesReturnOperationItem> items, CancellationToken cancellationToken);
}

public sealed class MoySkladSalesReturnGateway(HttpClient httpClient, IVendorTokenClient tokens,
    IMoySkladRateLimiter limiter, IMoySkladResponseHandler responses) : IMoySkladSalesReturnGateway
{
    public async Task ValidateAsync(SalesReturnCallContext context, Guid mainId, RecreateSalesReturnItem item, CancellationToken cancellationToken)
    {
        var original = await GetAsync(context, $"entity/salesreturn/{item.OldDocumentId:D}", cancellationToken);
        RequireId(original, item.OldDocumentId);
        RequireAgent(original, item.DuplicateCounterpartyId);
        // Resolve the target even when there is no demand, account or contract to validate it through.
        RequireId(await GetAsync(context, $"entity/counterparty/{mainId:D}", cancellationToken), mainId);
        if (item.NewAgentAccountId is { } account)
            RequireId(await GetAsync(context, $"entity/counterparty/{mainId:D}/accounts/{account:D}", cancellationToken), account);
        if (item.NewContractId is { } contract)
        {
            var document = await GetAsync(context, $"entity/contract/{contract:D}", cancellationToken);
            RequireId(document, contract);
            RequireAgent(document, mainId);
        }
        if (item.Data.Demand is { } demand)
        {
            var id = Guid.Parse(new Uri(demand["meta"]!["href"]!.GetValue<string>()).AbsolutePath.Split('/').Last());
            var document = await GetAsync(context, $"entity/demand/{id:D}", cancellationToken);
            RequireId(document, id);
            RequireAgent(document, mainId);
        }
    }

    public async Task<bool> ExistsAsync(SalesReturnCallContext context, Guid oldId, CancellationToken cancellationToken)
    {
        try { RequireId(await GetAsync(context, $"entity/salesreturn/{oldId:D}", cancellationToken), oldId); return true; }
        catch (EgressException exception) when (exception.StatusCode == 404) { return false; }
    }

    public async Task DeleteAsync(SalesReturnCallContext context, Guid oldId, CancellationToken cancellationToken)
    {
        var body = await SendAsync(context, HttpMethod.Delete, $"entity/salesreturn/{oldId:D}", null, cancellationToken);
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            if (JsonNode.Parse(body) is JsonObject { Count: 0 }) return;
        }
        catch (JsonException) { }
        // The caller reconciles this ambiguous response by reading the original before creating.
        throw new EgressException(502, "INVALID_DELETE_RESPONSE", "Deletion outcome requires verification.", retryable: true);
    }

    public async Task<IReadOnlyList<SalesReturnCreated>> CreateAsync(SalesReturnCallContext context, Guid mainId,
        IReadOnlyList<SalesReturnOperationItem> items, CancellationToken cancellationToken)
    {
        var body = "[" + string.Join(",", items.Select(x => x.Payload)) + "]";
        var json = await SendAsync(context, HttpMethod.Post, "entity/salesreturn/batch", body, cancellationToken);
        JsonArray rows;
        try { rows = JsonNode.Parse(json) as JsonArray ?? throw new JsonException(); }
        catch (JsonException) { throw new EgressException(502, "INVALID_CREATE_RESPONSE", "Expected a salesreturn response array.", retryable: true); }
        var results = new List<SalesReturnCreated>();
        foreach (var item in items)
        {
            var matches = rows.OfType<JsonObject>().Where(row => ReadGuid(row["syncId"]) == item.SyncId).ToArray();
            if (matches.Length != 1)
            {
                // A failed bulk row need not carry syncId. Retry unresolved items in singleton batches
                // so a permanent rejection can be attributed without assuming bulk response order.
                var rejected = items.Count == 1 && rows.Count == 1 && rows[0] is JsonObject error && error["errors"] is JsonArray;
                results.Add(new(item.SyncId, null, rejected ? "SALESRETURN_CREATE_REJECTED" : "CREATE_OUTCOME_UNKNOWN",
                    rejected ? "MoySklad rejected creation of this salesreturn." : "Creation result could not be matched by syncId.", !rejected));
                continue;
            }
            var row = matches[0];
            var id = ReadGuid(row["id"]);
            try
            {
                if (row["errors"] is not null) throw Invalid("MoySklad returned errors for the created salesreturn.");
                if (id is null || id == Guid.Empty || id == item.OldDocumentId ||
                    rows.OfType<JsonObject>().Count(other => ReadGuid(other["id"]) == id) != 1)
                    throw Invalid("Invalid or duplicate new salesreturn id.");
                RequireAgent(row, mainId);
                results.Add(new(item.SyncId, id));
            }
            catch (EgressException exception)
            {
                results.Add(new(item.SyncId, id, exception.Code, exception.SafeMessage, false));
            }
        }
        return results;
    }

    private async Task<JsonObject> GetAsync(SalesReturnCallContext context, string path, CancellationToken cancellationToken)
    {
        var body = await SendAsync(context, HttpMethod.Get, path, null, cancellationToken);
        try { return JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw Invalid("Expected a MoySklad object response."); }
    }

    private async Task<string> SendAsync(SalesReturnCallContext context, HttpMethod method, string path, string? body, CancellationToken cancellationToken)
    {
        var requestContext = new MoySkladRequestContext(context.AccountId, context.CorrelationId, context.JobId,
            context.OperationId, method.Method, path, "salesreturn", null);
        var watch = Stopwatch.StartNew();
        try
        {
            var token = await tokens.GetAccessTokenAsync(context.AccountId, cancellationToken);
            await limiter.WaitAsync(context.AccountId, context.UserId, cancellationToken);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", context.CorrelationId);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await limiter.ObserveAsync(context.AccountId, response, cancellationToken);
            // No business payloads are sent to the diagnostic body logger for this operation.
            if (!response.IsSuccessStatusCode)
            {
                using var safeResponse = new HttpResponseMessage(response.StatusCode)
                    { Content = new StringContent("{\"errors\":[{\"error\":\"MoySklad rejected the salesreturn request.\"}]}") };
                await responses.ReadAsync(safeResponse, requestContext, watch.Elapsed, cancellationToken);
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw responses.TransportFailure(requestContext, "MoySklad request timed out.", watch.Elapsed, exception); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        { throw responses.TransportFailure(requestContext, "MoySklad is unavailable.", watch.Elapsed, exception); }
    }

    private static Guid? ReadGuid(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;
    private static void RequireId(JsonObject document, Guid expected)
    { if (ReadGuid(document["id"]) != expected) throw Invalid("MoySklad returned an unexpected entity id."); }
    private void RequireAgent(JsonObject document, Guid expected)
    {
        if (document["agent"] is not JsonObject agent || agent["meta"] is not JsonObject meta ||
            meta["type"]?.ToString() != "counterparty" ||
            !Uri.TryCreate(meta["href"]?.ToString(), UriKind.Absolute, out var actual) ||
            actual.GetLeftPart(UriPartial.Path) != new Uri(httpClient.BaseAddress!, $"entity/counterparty/{expected:D}").AbsoluteUri)
            throw Invalid("The entity does not belong to the expected counterparty.");
    }
    private static EgressException Invalid(string message) => new(422, "SALESRETURN_PRECONDITION_FAILED", message, retryable: false);
}
