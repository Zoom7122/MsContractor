using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Options;

namespace MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

public sealed class PurchaseReturnCreateMapper
{
    private static readonly HashSet<string> AllowedDocumentFields =
    [
        "organization", "organizationAccount", "store", "supply", "moment", "applicable", "shared",
        "name", "code", "externalCode", "description", "rate", "vatEnabled", "vatIncluded",
        "state", "project", "owner", "group", "attributes"
    ];

    private static readonly string[] PositionFields =
    ["assortment", "quantity", "price", "discount", "vat", "vatEnabled", "pack", "slot", "things"];

    private static readonly HashSet<string> ExcludedFields =
    [
        "meta", "id", "accountId", "created", "updated", "deleted", "printed", "published",
        "sum", "vatSum", "payedSum", "positions", "agent", "contract", "agentAccount", "syncId",
        "factureIn", "factureOut", "payments", "files"
    ];

    private readonly Uri _jsonApiBaseUrl;

    public PurchaseReturnCreateMapper(IOptions<EgressOptions> options)
    {
        _jsonApiBaseUrl = options.Value.JsonApiBaseUrl;
    }

    public string BuildPayload(
        string sourceRawJson,
        IReadOnlyDictionary<Guid, string> positions,
        Guid mainCounterpartyId)
        => BuildPayload(
            sourceRawJson,
            positions,
            mainCounterpartyId,
            Guid.NewGuid(),
            new PurchaseReturnRecreationReferences(null, null));

    public string BuildPayload(
        string sourceRawJson,
        IReadOnlyDictionary<Guid, string> positions,
        Guid mainCounterpartyId,
        Guid syncId,
        PurchaseReturnRecreationReferences recreationReferences)
    {
        if (mainCounterpartyId == Guid.Empty)
            throw new ArgumentException("A non-empty main counterparty id is required.", nameof(mainCounterpartyId));
        if (syncId == Guid.Empty)
            throw new ArgumentException("A non-empty sync id is required.", nameof(syncId));

        var source = ParseObject(sourceRawJson);
        var payload = new JsonObject();

        foreach (var property in source)
        {
            if (ExcludedFields.Contains(property.Key) || !AllowedDocumentFields.Contains(property.Key))
                continue;

            if (property.Key == "supply")
            {
                if (property.Value is null)
                    continue;
                if (property.Value is not JsonObject supplyReference ||
                    supplyReference["meta"] is not JsonObject)
                    throw new InvalidOperationException("Saved purchasereturn contains an invalid supply reference.");

                payload[property.Key] = CleanReference(supplyReference);
                continue;
            }

            if (property.Value is JsonObject reference && reference["meta"] is JsonObject)
                payload[property.Key] = CleanReference(reference);
            else if (property.Key == "rate" && property.Value is JsonObject rate)
                payload[property.Key] = CleanRate(rate);
            else if (property.Key == "attributes" && property.Value is JsonArray attributes)
                payload[property.Key] = CleanAttributes(attributes);
            else
                payload[property.Key] = CleanValue(property.Value);
        }

        payload["agent"] = Reference(
            new Uri(_jsonApiBaseUrl, $"entity/counterparty/{mainCounterpartyId:D}").AbsoluteUri,
            "counterparty");

        if (source["contract"] is JsonObject && recreationReferences.ContractId is Guid contractId)
        {
            payload["contract"] = Reference(
                new Uri(_jsonApiBaseUrl, $"entity/contract/{contractId:D}").AbsoluteUri,
                "contract");
        }

        if (source["agentAccount"] is JsonObject && recreationReferences.AgentAccountId is Guid agentAccountId)
        {
            payload["agentAccount"] = Reference(
                new Uri(_jsonApiBaseUrl, $"entity/counterparty/{mainCounterpartyId:D}/accounts/{agentAccountId:D}").AbsoluteUri,
                "account");
        }

        payload["syncId"] = syncId.ToString("D");

        payload["positions"] = BuildPositions(positions);

        RequireReference(payload, "organization");
        RequireReference(payload, "store");
        if (positions.Count == 0)
            throw new InvalidOperationException("A purchasereturn requires at least one saved position.");

        return payload.ToJsonString();
    }

    private static JsonArray BuildPositions(IReadOnlyDictionary<Guid, string> positions)
    {
        var result = new JsonArray();
        foreach (var rawPosition in positions.OrderBy(item => item.Key).Select(item => item.Value))
            result.Add(CleanPosition(ParseObject(rawPosition)));
        return result;
    }

    private static JsonObject CleanPosition(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var field in PositionFields)
        {
            if (!source.TryGetPropertyValue(field, out var value))
                continue;
            if (field is "assortment" or "pack" or "slot")
            {
                if (value is not JsonObject reference)
                    throw new InvalidOperationException($"A purchasereturn position has an invalid {field} reference.");
                result[field] = CleanReference(reference);
            }
            else
            {
                result[field] = CleanValue(value);
            }
        }

        if (result["assortment"] is not JsonObject)
            throw new InvalidOperationException("A purchasereturn position does not contain assortment.");
        return result;
    }

    private static JsonObject CleanRate(JsonObject source)
    {
        var result = new JsonObject();
        if (source.TryGetPropertyValue("value", out var value))
            result["value"] = value?.DeepClone();
        if (source["currency"] is JsonObject currency)
            result["currency"] = CleanReference(currency);
        return result;
    }

    private static JsonArray CleanAttributes(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            if (item is not JsonObject attribute)
                throw new InvalidOperationException("A purchasereturn attribute is not an object.");
            var clean = new JsonObject();
            if (attribute["meta"] is JsonObject meta)
            {
                if (meta["href"] is not JsonValue href ||
                    meta["type"] is not JsonValue type)
                    throw new InvalidOperationException("A purchasereturn attribute has an invalid meta reference.");

                clean["meta"] = new JsonObject
                {
                    ["href"] = href.GetValue<string>(),
                    ["type"] = type.GetValue<string>(),
                    ["mediaType"] = "application/json"
                };
            }
            if (attribute.TryGetPropertyValue("value", out var value))
                clean["value"] = value?.DeepClone();
            result.Add(clean);
        }
        return result;
    }

    private static JsonNode? CleanValue(JsonNode? value)
    {
        if (value is JsonObject reference && reference["meta"] is JsonObject)
            return CleanReference(reference);
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var property in obj)
                result[property.Key] = CleanValue(property.Value);
            return result;
        }
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array)
                result.Add(CleanValue(item));
            return result;
        }
        return value?.DeepClone();
    }

    private static JsonObject ParseObject(string rawJson) =>
        JsonNode.Parse(rawJson)?.AsObject()
        ?? throw new InvalidOperationException("Saved purchasereturn data is not a JSON object.");

    private static void RequireReference(JsonObject source, string name)
    {
        if (source[name] is not JsonObject reference || reference["meta"] is not JsonObject)
            throw new InvalidOperationException($"Saved purchasereturn data does not contain {name}.");
    }

    private static JsonObject CleanReference(JsonObject reference)
    {
        if (reference["meta"] is not JsonObject meta ||
            meta["href"] is not JsonValue href ||
            meta["type"] is not JsonValue type)
            throw new InvalidOperationException("Saved purchasereturn contains an invalid entity reference.");

        return Reference(href.GetValue<string>(), type.GetValue<string>());
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
}
