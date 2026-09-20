using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MergeVerifier.Normalization;

public static class DocumentNormalizer
{
    private static readonly HashSet<string> CommonTechnical = new(StringComparer.OrdinalIgnoreCase)
    {
        "meta", "accountId", "updated", "created", "deleted", "printed", "published", "lastPrinted"
    };
    // These are exactly the fields removed by the current SalesReturnRecreationPayloadBuilder,
    // plus agent/contract fields deliberately replaced or removed by recreate.
    private static readonly HashSet<string> SalesReturnRecreateTechnical = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "meta", "accountId", "created", "updated", "deleted", "printed", "published", "sum", "vatSum",
        "payedSum", "returnedSum", "lastPrinted", "syncId", "agent", "contract", "agentAccount", "positions"
    };

    public static (string Json, string Hash) Normalize(string documentType, string rawJson,
        IReadOnlyList<string> positions, bool recreated)
    {
        var root = JsonNode.Parse(rawJson)?.AsObject() ?? throw new JsonException("Document JSON must be an object.");
        var excluded = recreated && string.Equals(documentType, "salesreturn", StringComparison.Ordinal)
            ? SalesReturnRecreateTechnical
            : CommonTechnical;
        foreach (var name in excluded) root.Remove(name);
        // Agent is validated separately by ReassignedDocumentComparer and intentionally changes in both flows.
        root.Remove("agent");
        root.Remove("positions");
        root["positions"] = new JsonArray(positions.Select(position =>
            JsonNode.Parse(position) ?? throw new JsonException("Position JSON is invalid.")).ToArray());
        var canonical = JsonCanonicalizer.Canonicalize(root);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return (canonical, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    public static string NormalizeCounterparty(string rawJson) =>
        JsonCanonicalizer.Canonicalize(JsonNode.Parse(rawJson) ?? throw new JsonException("Counterparty JSON is invalid."));
}

public static class JsonCanonicalizer
{
    public static string Canonicalize(JsonNode node)
    {
        var canonical = CanonicalizeNode(node) ?? throw new JsonException("JSON node cannot be null.");
        return canonical.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonArray array)
            return new JsonArray(array.Select(CanonicalizeNode).ToArray());
        if (node is not JsonObject source) return node.DeepClone();

        // Entity references include environment-specific hrefs. Preserve their type and entity id instead.
        if (source.TryGetPropertyValue("href", out var hrefNode) && hrefNode is JsonValue hrefValue &&
            hrefValue.TryGetValue<string>(out var href) && source.TryGetPropertyValue("type", out var typeNode) &&
            typeNode is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) &&
            Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            var candidate = uri.AbsolutePath.TrimEnd('/').Split('/').Last();
            if (Guid.TryParse(candidate, out var id))
                return new JsonObject { ["id"] = id.ToString("D"), ["type"] = type };
        }

        var result = new JsonObject();
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            result[pair.Key] = CanonicalizeNode(pair.Value);
        return result;
    }
}
