using System.Text.Json;
using System.Text.Json.Nodes;

namespace MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

public sealed class SalesReturnRecreationPayloadBuilder
{
    private static readonly string[] TechnicalDocumentFields =
    [
        "id", "meta", "accountId", "created", "updated", "deleted", "printed", "published",
        "sum", "vatSum", "payedSum", "returnedSum", "lastPrinted", "positions", "syncId"
    ];

    private static readonly string[] PositionFields =
    [
        "assortment", "quantity", "price", "discount", "vat", "vatEnabled", "pack", "things",
        "trackingCodes", "cost", "country", "gtd", "overhead", "slot"
    ];

    public string BuildNewPayload(string sourceRawJson, IReadOnlyDictionary<Guid, string> positions,
        Guid mainAgentId, Guid? targetAgentAccountId, Guid syncId)
    {
        var payload = BuildBase(sourceRawJson, positions, syncId);
        var sourceAgent = RequireReference(payload, "agent");
        payload["agent"] = Reference(RewriteEntityHref(sourceAgent, $"entity/counterparty/{mainAgentId:D}"), "counterparty");
        payload.Remove("contract");
        payload.Remove("agentAccount");
        if (targetAgentAccountId is { } agentAccountId)
            payload["agentAccount"] = Reference(
                RewriteEntityHref(sourceAgent, $"entity/counterparty/{mainAgentId:D}/accounts/{agentAccountId:D}"),
                "account");
        return payload.ToJsonString();
    }

    public Guid? ReadDemandId(string sourceRawJson) =>
        TryReadReferenceId(ParseObject(sourceRawJson)["demand"] as JsonObject);

    public void ValidateSource(string sourceRawJson, IReadOnlyDictionary<Guid, string> positions)
    {
        var source = ParseObject(sourceRawJson);
        _ = RequireReference(source, "agent");
        _ = RequireReference(source, "organization");
        _ = RequireReference(source, "store");
        if (source.TryGetPropertyValue("demand", out var demand) && demand is not null)
            _ = RequireReference(source, "demand");
    }

    private JsonObject BuildBase(string sourceRawJson, IReadOnlyDictionary<Guid, string> positions, Guid syncId)
    {
        var payload = ParseObject(sourceRawJson).DeepClone().AsObject();
        foreach (var field in TechnicalDocumentFields)
            payload.Remove(field);

        foreach (var reference in new[]
                 {
                     "agent", "organization", "organizationAccount", "store", "demand", "project", "state",
                     "salesChannel", "owner", "group", "contract", "agentAccount"
                 })
        {
            if (payload[reference] is JsonObject value)
                payload[reference] = CleanReference(value);
        }

        var rows = new JsonArray();
        foreach (var rawPosition in positions.OrderBy(item => item.Key).Select(item => item.Value))
            rows.Add(CleanPosition(ParseObject(rawPosition)));
        payload["positions"] = rows;
        payload["syncId"] = syncId.ToString();
        return payload;
    }

    private static JsonObject ParseObject(string rawJson) => JsonNode.Parse(rawJson)?.AsObject()
        ?? throw new InvalidOperationException("Saved salesreturn data is not a JSON object.");

    private static JsonObject RequireReference(JsonObject source, string name) => source[name] as JsonObject
        ?? throw new InvalidOperationException($"Saved salesreturn data does not contain {name}.");

    private static JsonObject CleanPosition(JsonObject? source)
    {
        if (source is null)
            throw new InvalidOperationException("Position is not an object.");
        var result = new JsonObject();
        foreach (var field in PositionFields)
        {
            if (source.TryGetPropertyValue(field, out var value))
                result[field] = value?.DeepClone();
        }
        if (result["assortment"] is not JsonObject assortment)
            throw new InvalidOperationException("A salesreturn position does not contain assortment.");
        result["assortment"] = CleanReference(assortment);
        foreach (var field in new[] { "country", "slot" })
        {
            if (result[field] is JsonObject reference)
                result[field] = CleanReference(reference);
        }
        return result;
    }

    private static JsonObject CleanReference(JsonObject reference)
    {
        if (reference["meta"] is not JsonObject meta || meta["href"] is not JsonValue href || meta["type"] is not JsonValue type)
            throw new InvalidOperationException("Saved salesreturn contains an invalid entity reference.");
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

    private static Guid? TryReadReferenceId(JsonObject? reference)
    {
        if (reference?["meta"]?["href"] is not JsonValue href ||
            !Uri.TryCreate(href.GetValue<string>(), UriKind.Absolute, out var uri))
            return null;
        return Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var id) ? id : null;
    }

    private static string RewriteEntityHref(JsonObject reference, string entityPath)
    {
        if (reference["meta"]?["href"] is not JsonValue href ||
            !Uri.TryCreate(href.GetValue<string>(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Saved salesreturn contains an invalid agent URL.");
        var entityIndex = uri.AbsolutePath.IndexOf("/entity/", StringComparison.Ordinal);
        if (entityIndex < 0)
            throw new InvalidOperationException("Saved salesreturn agent URL is not a MoySklad entity URL.");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..entityIndex] + "/" + entityPath;
    }
}
