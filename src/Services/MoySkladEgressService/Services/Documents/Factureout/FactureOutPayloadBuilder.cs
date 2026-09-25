using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public interface IFactureOutPayloadBuilder
{
    Task<FactureOutPayloadBuildResult> BuildAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyCollection<Guid> factureOutIds,
        CancellationToken cancellationToken);
}

public sealed class FactureOutPayloadBuilder : IFactureOutPayloadBuilder
{
    private static readonly string[] RemovedFields =
    [
        "meta", "created", "deleted", "id", "printed", "published", "sum", "updated", "syncId"
    ];

    private readonly IFactureOutRawDataRepository _rawDataRepository;
    private readonly IFactureOutRecreationItemRepository _recreationItems;

    public FactureOutPayloadBuilder(
        IFactureOutRawDataRepository rawDataRepository,
        IFactureOutRecreationItemRepository recreationItems)
    {
        _rawDataRepository = rawDataRepository;
        _recreationItems = recreationItems;
    }

    public async Task<FactureOutPayloadBuildResult> BuildAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyCollection<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, mainCounterpartyId, factureOutIds);

        if (factureOutIds.Count == 0)
            return new FactureOutPayloadBuildResult([], []);

        var rawDocuments = await _rawDataRepository.GetAsync(
            accountId,
            factureOutIds,
            cancellationToken);
        var payloads = new List<FactureOutPayload>(factureOutIds.Count);
        var skipped = new List<FactureOutSkippedDocumentResult>();
        var newSyncIds = new Dictionary<Guid, Guid>();

        foreach (var documentId in factureOutIds)
        {
            if (!rawDocuments.TryGetValue(documentId, out var rawJson) ||
                string.IsNullOrWhiteSpace(rawJson))
            {
                skipped.Add(Skip(documentId, "Raw factureout JSON is missing."));
                continue;
            }

            try
            {
                var newSyncId = Guid.NewGuid();
                payloads.Add(new FactureOutPayload(
                    documentId,
                    BuildPayload(rawJson, mainCounterpartyId, newSyncId))
                {
                    NewSyncId = newSyncId
                });
                newSyncIds.Add(documentId, newSyncId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                skipped.Add(Skip(documentId, exception.Message));
            }
        }

        await _recreationItems.SavePayloadBuildResultsAsync(
            accountId,
            newSyncIds,
            skipped.Select(item => item.DocumentId).ToArray(),
            cancellationToken);

        return new FactureOutPayloadBuildResult(payloads, skipped);
    }

    private static string BuildPayload(string rawJson, Guid mainCounterpartyId, Guid newSyncId)
    {
        var payload = ParseObject(rawJson).DeepClone().AsObject();
        foreach (var field in RemovedFields)
            payload.Remove(field);

        var sourceAgent = payload["agent"] as JsonObject
            ?? throw new InvalidOperationException("Saved factureout does not contain agent.");
        var agentHref = ReadAgentHref(sourceAgent);
        payload["agent"] = Reference(
            BuildCounterpartyHref(agentHref, mainCounterpartyId),
            "counterparty");
        payload["syncId"] = newSyncId.ToString("D");

        return payload.ToJsonString();
    }

    private static JsonObject ParseObject(string rawJson) => JsonNode.Parse(rawJson)?.AsObject()
        ?? throw new InvalidOperationException("Saved factureout data is not a JSON object.");

    private static string ReadAgentHref(JsonObject agent)
    {
        if (agent["meta"] is not JsonObject meta ||
            meta["href"] is not JsonValue href ||
            !Uri.TryCreate(href.GetValue<string>(), UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Saved factureout contains an invalid agent URL.");
        }

        var entityIndex = uri.AbsolutePath.IndexOf("/entity/", StringComparison.Ordinal);
        var sourceId = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        if (entityIndex < 0 || !Guid.TryParse(sourceId, out var parsedId) || parsedId == Guid.Empty)
            throw new InvalidOperationException("Saved factureout agent URL is not a valid MoySklad entity reference.");

        return uri.ToString();
    }

    private static string BuildCounterpartyHref(string sourceAgentHref, Guid mainCounterpartyId)
    {
        var uri = new Uri(sourceAgentHref, UriKind.Absolute);
        var entityIndex = uri.AbsolutePath.IndexOf("/entity/", StringComparison.Ordinal);
        if (entityIndex < 0)
            throw new InvalidOperationException("Saved factureout agent URL is not a MoySklad entity URL.");

        return uri.GetLeftPart(UriPartial.Authority) +
            uri.AbsolutePath[..entityIndex] +
            $"/entity/counterparty/{mainCounterpartyId:D}";
    }

    private static JsonObject Reference(string href, string type) => new()
    {
        ["meta"] = new JsonObject
        {
            ["href"] = href,
            ["type"] = type,
            ["mediaType"] = "application/json"
        }
    };

    private static FactureOutSkippedDocumentResult Skip(Guid documentId, string error) =>
        new(documentId, "Skipped", "FACTUREOUT_PAYLOAD_INVALID", error);

    private static void ValidateInput(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyCollection<Guid> factureOutIds)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        if (mainCounterpartyId == Guid.Empty)
            throw new ArgumentException("A non-empty main counterparty id is required.", nameof(mainCounterpartyId));
        if (factureOutIds.Any(id => id == Guid.Empty) ||
            factureOutIds.Distinct().Count() != factureOutIds.Count)
        {
            throw new ArgumentException(
                "Factureout ids must be non-empty and unique.",
                nameof(factureOutIds));
        }
    }
}
