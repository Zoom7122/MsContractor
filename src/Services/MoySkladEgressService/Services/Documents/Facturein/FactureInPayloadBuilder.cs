using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Facturein;

public interface IFactureInPayloadBuilder
{
    Task<FactureInPayloadBuildResult> BuildAsync(Guid accountId, Guid mainCounterpartyId,
        IReadOnlyCollection<Guid> factureInIds, CancellationToken cancellationToken);
}

public sealed class FactureInPayloadBuilder : IFactureInPayloadBuilder
{
    private static readonly string[] RemovedFields =
    [
        "meta", "id", "accountId", "created", "updated", "deleted", "printed", "published", "files", "sum", "syncId", "contract"
    ];

    private static readonly IReadOnlySet<string> SupplyBaseTypes =
        new HashSet<string>(["supply"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> PaymentBaseTypes =
        new HashSet<string>(["paymentout", "cashout"], StringComparer.OrdinalIgnoreCase);

    private readonly IFactureInRawDataRepository _rawData;
    private readonly IFactureInRecreationItemRepository _items;
    private readonly IMoySkladFactureInGateway _gateway;

    public FactureInPayloadBuilder(IFactureInRawDataRepository rawData,
        IFactureInRecreationItemRepository items, IMoySkladFactureInGateway gateway)
    {
        _rawData = rawData;
        _items = items;
        _gateway = gateway;
    }

    public async Task<FactureInPayloadBuildResult> BuildAsync(Guid accountId, Guid mainCounterpartyId,
        IReadOnlyCollection<Guid> factureInIds, CancellationToken cancellationToken)
    {
        var existing = await _items.GetAsync(accountId, factureInIds, cancellationToken);
        var prepared = new List<FactureInRecreationItem>();
        var completed = new List<FactureInRecreationItem>();
        var failed = new List<FactureInFailedDocumentResult>();
        var freshIds = new List<Guid>();

        foreach (var documentId in factureInIds)
        {
            if (!existing.TryGetValue(documentId, out var item))
            {
                freshIds.Add(documentId);
                continue;
            }

            if (item.MainCounterpartyId != mainCounterpartyId)
            {
                failed.Add(Failed(item, "FACTUREIN_RECREATION_MAIN_COUNTERPARTY_MISMATCH",
                    "The facturein recreation was started for another main counterparty."));
                continue;
            }

            switch (item.Stage)
            {
                case "Completed": completed.Add(item); break;
                case "ResponseMappingFailed": failed.Add(Failed(item,
                    item.ErrorCode ?? "FACTUREIN_RESPONSE_MAPPING_FAILED",
                    item.Error ?? "The created facturein could not be mapped safely.")); break;
                default: prepared.Add(item); break;
            }
        }

        var skipped = new List<FactureInSkippedDocumentResult>();
        if (freshIds.Count == 0)
            return new FactureInPayloadBuildResult(prepared, skipped, completed, failed);

        IReadOnlyDictionary<Guid, string> rawDocuments;
        try
        {
            rawDocuments = await _rawData.GetRequiredAsync(accountId, freshIds, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            skipped.AddRange(freshIds.Select(id => Skip(id, "FACTUREIN_PAYLOAD_INVALID", exception.Message)));
            return new FactureInPayloadBuildResult(prepared, skipped, completed, failed);
        }

        var candidates = new List<BuildCandidate>();
        foreach (var documentId in freshIds)
        {
            try
            {
                var source = ParseObject(rawDocuments[documentId]);
                var bases = ReadBases(source);
                candidates.Add(new BuildCandidate(documentId, rawDocuments[documentId], source, bases));
            }
            catch (UnsupportedBaseTypeException exception)
            {
                skipped.Add(Skip(documentId, "FACTUREIN_UNSUPPORTED_BASE_TYPE", exception.Message));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                skipped.Add(Skip(documentId, "FACTUREIN_PAYLOAD_INVALID", exception.Message));
            }
        }

        var knownBases = await _gateway.GetExistingBasesAsync(accountId, Guid.NewGuid().ToString("D"),
            candidates.SelectMany(item => item.Bases).Distinct().ToArray(), cancellationToken);
        foreach (var candidate in candidates)
        {
            if (candidate.Bases.Count == 0 || candidate.Bases.Any(reference => !knownBases.Contains(reference)))
            {
                skipped.Add(Skip(candidate.DocumentId, "FACTUREIN_BASE_DOCUMENT_MISSING",
                    "One or more facturein base documents are unavailable."));
                continue;
            }

            try
            {
                var newSyncId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var item = new FactureInRecreationItem
                {
                    AccountId = accountId,
                    SourceFactureInId = candidate.DocumentId,
                    MainCounterpartyId = mainCounterpartyId,
                    SourceSyncId = TryReadSyncId(candidate.Source),
                    NewSyncId = newSyncId,
                    PayloadJson = BuildPayload(candidate.RawJson, mainCounterpartyId, newSyncId),
                    Stage = "Prepared",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                item = await _items.CreateOrGetAsync(item, cancellationToken);
                prepared.Add(item);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                skipped.Add(Skip(candidate.DocumentId, "FACTUREIN_PAYLOAD_INVALID", exception.Message));
            }
        }

        return new FactureInPayloadBuildResult(prepared, skipped, completed, failed);
    }

    private static IReadOnlyList<FactureInBaseReference> ReadBases(JsonObject source)
    {
        var bases = new List<FactureInBaseReference>();
        ReadBaseCollection(source, "supplies", SupplyBaseTypes, bases);
        ReadBaseCollection(source, "payments", PaymentBaseTypes, bases);
        return bases;
    }

    private static void ReadBaseCollection(JsonObject source, string field, IReadOnlySet<string> expectedTypes,
        ICollection<FactureInBaseReference> result)
    {
        if (!source.TryGetPropertyValue(field, out var node) || node is null)
            return;
        if (node is not JsonArray values)
            throw new InvalidOperationException($"Saved facturein {field} is not an array.");
        foreach (var value in values)
        {
            if (value is not JsonObject reference)
                throw new InvalidOperationException($"Saved facturein {field} contains a non-reference item.");
            var (type, documentId) = ReadReference(reference);
            if (!expectedTypes.Contains(type))
                throw new UnsupportedBaseTypeException($"Saved facturein {field} contains unsupported base type {type}.");
            result.Add(new FactureInBaseReference(type.ToLowerInvariant(), documentId));
        }
    }

    private static string BuildPayload(string rawJson, Guid mainCounterpartyId, Guid newSyncId)
    {
        var payload = ParseObject(rawJson).DeepClone().AsObject();
        foreach (var field in RemovedFields)
            payload.Remove(field);

        _ = ReadReference(RequireReference(payload, "organization"));
        var sourceAgent = RequireReference(payload, "agent");
        var agentHref = RewriteEntityHref(sourceAgent, $"entity/counterparty/{mainCounterpartyId:D}");
        payload["agent"] = Reference(agentHref, "counterparty");
        payload["syncId"] = newSyncId.ToString("D");
        return payload.ToJsonString();
    }

    private static JsonObject ParseObject(string rawJson) => JsonNode.Parse(rawJson)?.AsObject()
        ?? throw new InvalidOperationException("Saved facturein data is not a JSON object.");

    private static JsonObject RequireReference(JsonObject source, string field) => source[field] as JsonObject
        ?? throw new InvalidOperationException($"Saved facturein does not contain {field}.");

    private static (string Type, Guid DocumentId) ReadReference(JsonObject reference)
    {
        if (reference["meta"] is not JsonObject meta || meta["href"] is not JsonValue href ||
            meta["type"] is not JsonValue type || !Uri.TryCreate(href.GetValue<string>(), UriKind.Absolute, out var uri) ||
            !Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Saved facturein contains an invalid entity reference.");
        return (type.GetValue<string>(), id);
    }

    private static Guid? TryReadSyncId(JsonObject source) => source["syncId"] is JsonValue value &&
        Guid.TryParse(value.GetValue<string>(), out var id) && id != Guid.Empty ? id : null;

    private static JsonObject Reference(string href, string type) => new()
    {
        ["meta"] = new JsonObject { ["href"] = href, ["type"] = type, ["mediaType"] = "application/json" }
    };

    private static string RewriteEntityHref(JsonObject reference, string entityPath)
    {
        if (reference["meta"]?["href"] is not JsonValue href ||
            !Uri.TryCreate(href.GetValue<string>(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Saved facturein contains an invalid agent URL.");
        var entityIndex = uri.AbsolutePath.IndexOf("/entity/", StringComparison.Ordinal);
        if (entityIndex < 0)
            throw new InvalidOperationException("Saved facturein agent URL is not a MoySklad entity URL.");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..entityIndex] + "/" + entityPath;
    }

    private static FactureInSkippedDocumentResult Skip(Guid id, string code, string error) =>
        new(id, "Skipped", code, error);

    private static FactureInFailedDocumentResult Failed(FactureInRecreationItem item, string code, string error) =>
        new(item.SourceFactureInId, item.NewSyncId, item.NewFactureInId, "Failed", code, error);

    private sealed record BuildCandidate(Guid DocumentId, string RawJson, JsonObject Source,
        IReadOnlyList<FactureInBaseReference> Bases);

    private sealed class UnsupportedBaseTypeException(string message) : InvalidOperationException(message);
}
