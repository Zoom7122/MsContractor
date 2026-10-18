using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

public sealed record PurchaseReturnFactureOutPreparationResult(
    IReadOnlyDictionary<Guid, IReadOnlyList<FactureOutPayload>> PayloadsByPurchaseReturn,
    IReadOnlyList<PurchaseReturnDocumentVerificationResult> SkippedDocuments,
    IReadOnlyList<FactureOutSkippedDocumentResult> SkippedFactureOuts);

public sealed record PurchaseReturnFactureOutCreationResult(
    IReadOnlyList<RecreatedPurchaseReturnFactureOut> RecreatedDocuments,
    IReadOnlyList<PurchaseReturnDocumentVerificationResult> FailedDocuments);

public interface IPurchaseReturnFactureOutService
{
    Task<PurchaseReturnFactureOutPreparationResult> PrepareAsync(Guid accountId, Guid mainCounterpartyId,
        IReadOnlyList<PurchaseReturnFactureOutRelation> relations, CancellationToken cancellationToken);
    Task<FactureOutDocumentDeletionResult> DeleteAsync(Guid accountId,
        IReadOnlyList<FactureOutPayload> payloads, CancellationToken cancellationToken);
    Task<PurchaseReturnFactureOutCreationResult> CreateAsync(Guid accountId, Guid mainCounterpartyId,
        IReadOnlyDictionary<Guid, IReadOnlyList<FactureOutPayload>> payloadsByPurchaseReturn,
        IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds, CancellationToken cancellationToken);
}

public sealed class PurchaseReturnFactureOutService : IPurchaseReturnFactureOutService
{
    private readonly IFactureOutPreparationService _preparation;
    private readonly IFactureOutPayloadBuilder _payloads;
    private readonly IFactureOutRawDataRepository _rawData;
    private readonly FactureOutDocumentRecreationService _recreation;
    private readonly IMoySkladFactureOutGateway _gateway;

    public PurchaseReturnFactureOutService(IFactureOutPreparationService preparation,
        IFactureOutPayloadBuilder payloads, IFactureOutRawDataRepository rawData,
        FactureOutDocumentRecreationService recreation, IMoySkladFactureOutGateway gateway)
    {
        _preparation = preparation;
        _payloads = payloads;
        _rawData = rawData;
        _recreation = recreation;
        _gateway = gateway;
    }

    public async Task<PurchaseReturnFactureOutPreparationResult> PrepareAsync(Guid accountId,
        Guid mainCounterpartyId, IReadOnlyList<PurchaseReturnFactureOutRelation> relations,
        CancellationToken cancellationToken)
    {
        var uniqueRelations = relations.Distinct().ToArray();
        var owners = uniqueRelations.GroupBy(item => item.FactureOutId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.PurchaseReturnId).ToArray());
        var payloads = new Dictionary<Guid, FactureOutPayload>();
        var skipped = new Dictionary<Guid, FactureOutSkippedDocumentResult>();
        foreach (var batch in owners.Keys.Chunk(MoySkladFactureOutGateway.BatchSize))
        {
            try
            {
                var preparation = await _preparation.PrepareAsync(accountId, batch, cancellationToken);
                foreach (var failure in preparation.SkippedDocuments)
                    skipped[failure.DocumentId] = failure;
                var preparedIds = preparation.DocumentIds.ToHashSet();
                var raw = await _rawData.GetAsync(accountId, batch, cancellationToken);
                var validIds = new List<Guid>();
                foreach (var id in batch)
                {
                    if (skipped.ContainsKey(id))
                        continue;
                    try
                    {
                        if (owners[id].Length != 1)
                            throw new InvalidOperationException("factureout is shared by multiple purchasereturn documents.");
                        if (!preparedIds.Contains(id) || !raw.TryGetValue(id, out var json))
                            throw new InvalidOperationException("Prepared factureout data is missing.");
                        ValidateReturn(ParseObject(json), owners[id][0]);
                        validIds.Add(id);
                    }
                    catch (Exception exception) when (IsControlled(exception))
                    {
                        skipped[id] = Skip(id, SafeError(exception));
                    }
                }

                if (validIds.Count == 0)
                    continue;
                var built = await _payloads.BuildAsync(accountId, mainCounterpartyId, validIds, cancellationToken);
                foreach (var failure in built.SkippedDocuments)
                    skipped[failure.DocumentId] = failure;
                foreach (var payload in built.Payloads)
                {
                    try
                    {
                        ValidateReturn(ParseObject(payload.PayloadJson), owners[payload.SourceDocumentId][0]);
                        payloads.Add(payload.SourceDocumentId, payload);
                    }
                    catch (Exception exception) when (IsControlled(exception))
                    {
                        skipped[payload.SourceDocumentId] = Skip(payload.SourceDocumentId, SafeError(exception));
                    }
                }
                foreach (var id in validIds.Where(id => !payloads.ContainsKey(id) && !skipped.ContainsKey(id)))
                    skipped[id] = Skip(id, "factureout payload is missing.");
            }
            catch (Exception exception) when (IsControlled(exception))
            {
                foreach (var id in batch)
                    skipped.TryAdd(id, Skip(id, SafeError(exception)));
            }
        }

        var ready = new Dictionary<Guid, IReadOnlyList<FactureOutPayload>>();
        var skippedReturns = new List<PurchaseReturnDocumentVerificationResult>();
        foreach (var group in uniqueRelations.GroupBy(item => item.PurchaseReturnId))
        {
            var invoiceIds = group.Select(item => item.FactureOutId).ToArray();
            var failures = invoiceIds.Where(skipped.ContainsKey).Select(id => skipped[id]).ToArray();
            if (failures.Length == 0)
            {
                ready.Add(group.Key, invoiceIds.Select(id => payloads[id]).ToArray());
                continue;
            }
            var error = string.Join(" | ", failures.Select(item => $"factureout {item.DocumentId:D}: {item.Reason}"));
            skippedReturns.Add(Failure(group.Key, null, "Skipped", "PURCHASERETURN_FACTUREOUT_PREPARATION_FAILED", error));
            foreach (var id in invoiceIds)
                skipped.TryAdd(id, Skip(id, "The related purchasereturn was skipped during preparation."));
        }
        return new PurchaseReturnFactureOutPreparationResult(ready, skippedReturns, skipped.Values.ToArray());
    }

    public async Task<FactureOutDocumentDeletionResult> DeleteAsync(Guid accountId,
        IReadOnlyList<FactureOutPayload> payloads, CancellationToken cancellationToken)
    {
        var deleted = new List<FactureOutPayload>();
        var failed = new List<FactureOutFailedDocumentResult>();
        var correlationId = Guid.NewGuid().ToString("D");
        foreach (var batch in payloads.Chunk(MoySkladFactureOutGateway.BatchSize))
        {
            var result = await _recreation.DeleteDocumentsAsync(accountId, correlationId, batch, cancellationToken);
            deleted.AddRange(result.ReadyToCreatePayloads);
            failed.AddRange(result.FailedDocuments);
        }
        return new FactureOutDocumentDeletionResult(deleted, failed);
    }

    public async Task<PurchaseReturnFactureOutCreationResult> CreateAsync(Guid accountId,
        Guid mainCounterpartyId, IReadOnlyDictionary<Guid, IReadOnlyList<FactureOutPayload>> payloadsByPurchaseReturn,
        IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds, CancellationToken cancellationToken)
    {
        var owners = new Dictionary<Guid, Guid>();
        var prepared = new List<FactureOutPayload>();
        var errors = new Dictionary<Guid, List<string>>();
        var recreated = new List<RecreatedPurchaseReturnFactureOut>();
        void Fail(Guid ownerId, Guid invoiceId, string reason)
        {
            if (!errors.TryGetValue(ownerId, out var messages))
                errors[ownerId] = messages = [];
            messages.Add($"factureout {invoiceId:D}: {reason}");
        }
        foreach (var (sourceId, newId) in oldToNewPurchaseReturnIds)
        {
            if (!payloadsByPurchaseReturn.TryGetValue(sourceId, out var invoices))
                continue;
            foreach (var invoice in invoices)
            {
                owners.Add(invoice.SourceDocumentId, sourceId);
                try
                {
                    prepared.Add(invoice with { PayloadJson = RebindReturn(invoice.PayloadJson, sourceId, newId) });
                }
                catch (Exception exception) when (IsControlled(exception))
                {
                    Fail(sourceId, invoice.SourceDocumentId, SafeError(exception));
                }
            }
        }
        var correlationId = Guid.NewGuid().ToString("D");
        foreach (var batch in prepared.Chunk(MoySkladFactureOutGateway.BatchSize))
        {
            try
            {
                var result = await _recreation.CreateAndVerifyDocumentsAsync(
                    accountId, mainCounterpartyId, correlationId, batch, [], cancellationToken);
                foreach (var (sourceId, newId) in result.CreatedDocumentIds)
                    recreated.Add(new RecreatedPurchaseReturnFactureOut(owners[sourceId], sourceId, newId));
                foreach (var failure in result.FailedDocuments)
                    Fail(owners[failure.SourceDocumentId], failure.SourceDocumentId, failure.Error ?? "Creation failed.");
            }
            catch (Exception exception) when (IsControlled(exception))
            {
                foreach (var item in batch)
                    Fail(owners[item.SourceDocumentId], item.SourceDocumentId, SafeError(exception));
            }
        }
        foreach (var batch in recreated.Chunk(MoySkladFactureOutGateway.BatchSize))
        {
            try
            {
                var documents = await _gateway.GetAsync(accountId, correlationId,
                    batch.Select(item => item.NewDocumentId).ToArray(), cancellationToken);
                foreach (var item in batch)
                {
                    try
                    {
                        if (!documents.TryGetValue(item.NewDocumentId, out var json))
                            throw new InvalidOperationException("Created factureout was not returned for verification.");
                        var document = ParseObject(json);
                        ValidateReturn(document, oldToNewPurchaseReturnIds[item.SourcePurchaseReturnId]);
                        if (ReferenceId(document["agent"], "counterparty") != mainCounterpartyId)
                            throw new InvalidOperationException("Created factureout agent does not match the main counterparty.");
                    }
                    catch (Exception exception) when (IsControlled(exception))
                    {
                        Fail(item.SourcePurchaseReturnId, item.SourceDocumentId, SafeError(exception));
                    }
                }
            }
            catch (Exception exception) when (IsControlled(exception))
            {
                foreach (var item in batch)
                    Fail(item.SourcePurchaseReturnId, item.SourceDocumentId, SafeError(exception));
            }
        }
        return new PurchaseReturnFactureOutCreationResult(recreated, errors.Select(pair => Failure(
            pair.Key, oldToNewPurchaseReturnIds[pair.Key], "CreatedWithError",
            "PURCHASERETURN_FACTUREOUT_RECREATION_FAILED", string.Join(" | ", pair.Value))).ToArray());
    }

    private static JsonObject ParseObject(string json) => JsonNode.Parse(json) as JsonObject
        ?? throw new JsonException("factureout data is not a JSON object.");

    private static JsonArray Returns(JsonObject document)
    {
        if (document["returns"] is JsonArray array)
            return array;
        if (document["returns"] is JsonObject collection && collection["rows"] is JsonArray rows &&
            (collection["meta"]?["size"] is not JsonValue size || size.GetValue<int>() == rows.Count))
            return rows;
        throw new InvalidOperationException("factureout returns are missing or incomplete.");
    }

    private static void ValidateReturn(JsonObject document, Guid purchaseReturnId)
    {
        var returns = Returns(document);
        if (returns.Count != 1)
            throw new InvalidOperationException("factureout must contain exactly one returns entry.");
        if (ReferenceId(returns[0], "purchasereturn") != purchaseReturnId)
            throw new InvalidOperationException("factureout does not reference the expected purchasereturn.");
    }

    private static Guid ReferenceId(JsonNode? reference, string expectedType)
    {
        if (reference is not JsonObject obj || obj["meta"] is not JsonObject meta ||
            meta["type"]?.GetValue<string>() != expectedType ||
            !Uri.TryCreate(meta["href"]?.GetValue<string>(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid factureout relation reference.");
        var segments = uri.AbsolutePath.TrimEnd('/').Split('/');
        if (segments.Length < 2 || segments[^2] != expectedType ||
            !Guid.TryParse(segments[^1], out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Invalid factureout relation id.");
        return id;
    }

    private static string RebindReturn(string json, Guid sourceId, Guid newId)
    {
        var document = ParseObject(json);
        ValidateReturn(document, sourceId);
        var uri = new Uri(Returns(document)[0]!["meta"]!["href"]!.GetValue<string>());
        var path = uri.AbsolutePath.TrimEnd('/');
        var href = new UriBuilder(uri)
        {
            Path = path[..(path.LastIndexOf('/') + 1)] + newId.ToString("D"), Query = "", Fragment = ""
        }.Uri.ToString();
        document["returns"] = new JsonArray(new JsonObject
        {
            ["meta"] = new JsonObject
            {
                ["href"] = href,
                ["type"] = "purchasereturn",
                ["mediaType"] = "application/json"
            }
        });
        return document.ToJsonString();
    }

    private static bool IsControlled(Exception exception) =>
        exception is JsonException or InvalidOperationException or FormatException or EgressException;
    private static string SafeError(Exception exception) =>
        exception is EgressException egress ? egress.SafeMessage : exception.Message;
    private static FactureOutSkippedDocumentResult Skip(Guid id, string reason) =>
        new(id, "Skipped", "FACTUREOUT_PURCHASERETURN_PREPARATION_FAILED", reason);
    private static PurchaseReturnDocumentVerificationResult Failure(Guid id, Guid? newId,
        string status, string code, string error) => new(id, newId, status, [], [], [], code, error);
}
