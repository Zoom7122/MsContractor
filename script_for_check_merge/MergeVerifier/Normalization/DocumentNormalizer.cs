using System.Text.Json.Nodes;
using MergeVerifier.Documents;

namespace MergeVerifier.Normalization;

public delegate Task<string> RecreatedReferenceResolver(string entityType, Guid id, CancellationToken ct);

public interface IDocumentNormalizer
{
    string EntityType { get; }
    Task<JsonObject> NormalizeAsync(JsonObject document, RecreatedReferenceResolver resolver, CancellationToken ct);
}

public class DocumentNormalizer(DocumentRule rule) : IDocumentNormalizer
{
    public string EntityType => rule.EntityType;
    public static readonly string[] RootTechnicalFields = ["id", "accountId", "meta", "href", "uuidHref", "updated", "agent"];

    public static IDocumentNormalizer For(DocumentRule rule) => rule.EntityType switch
    {
        "salesreturn" => new SalesReturnNormalizer(rule),
        "purchasereturn" => new PurchaseReturnNormalizer(rule),
        "retailsalesreturn" => new RetailSalesReturnNormalizer(rule),
        _ => new DocumentNormalizer(rule)
    };

    public virtual async Task<JsonObject> NormalizeAsync(JsonObject document, RecreatedReferenceResolver resolver, CancellationToken ct)
    {
        var result = new JsonObject();
        foreach (var (key, value) in document)
        {
            if (RootTechnicalFields.Contains(key) || key == "created" && rule.TransferMode == DocumentTransferMode.Recreate) continue;
            if (rule.PositionCollections.Contains(key))
            {
                if (value is not JsonArray positions) throw new VerifierException("Positions were not fully loaded.");
                var normalized = new List<JsonNode?>();
                foreach (var position in positions)
                {
                    if (position is not JsonObject row) throw new VerifierException("Invalid document position.");
                    var copy = (JsonObject)row.DeepClone();
                    foreach (var technical in new[] { "id", "accountId", "meta" }) copy.Remove(technical);
                    normalized.Add(await NormalizeValue(copy, resolver, ct));
                }
                result[key] = JsonCanonicalizer.Multiset(normalized);
            }
            else if (key == "files" && value is JsonArray files)
            {
                var normalized = new List<JsonNode?>();
                foreach (var file in files)
                {
                    if (file is not JsonObject row) throw new VerifierException("Invalid file metadata.");
                    var copy = (JsonObject)row.DeepClone();
                    // Unlike document IDs, the files API documents shared identity for equal filename/content.
                    var href = copy["meta"]?["href"]?.GetValue<string>();
                    if (!Uri.TryCreate(href, UriKind.Absolute, out var fileUri) ||
                        !Guid.TryParse(fileUri.Segments.Last(), out var fileId))
                        throw new VerifierException("File metadata lacks verifiable content identity.");
                    copy["fileIdentity"] = fileId.ToString("D");
                    foreach (var technical in new[] { "id", "meta", "created", "download", "miniature", "tiny" }) copy.Remove(technical);
                    normalized.Add(await NormalizeValue(copy, resolver, ct));
                }
                result[key] = JsonCanonicalizer.Multiset(normalized);
            }
            else
            {
                var normalized = await NormalizeValue(value, resolver, ct);
                result[key] = normalized is JsonArray array && (rule.UnorderedCollections.Contains(key) || key == "attributes")
                    ? JsonCanonicalizer.Multiset(array) : normalized;
            }
        }
        return result;
    }

    private static async Task<JsonNode?> NormalizeValue(JsonNode? node, RecreatedReferenceResolver resolver, CancellationToken ct)
    {
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array) result.Add(await NormalizeValue(item, resolver, ct));
            return result;
        }
        if (node is not JsonObject obj) return node?.DeepClone();
        var output = new JsonObject();
        if (obj["meta"] is JsonObject meta)
        {
            if (meta.ContainsKey("size")) throw new VerifierException("Unloaded nested collection in document.");
            var href = meta["href"]?.GetValue<string>();
            var type = meta["type"]?.GetValue<string>();
            if (href is null || type is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
                throw new VerifierException("Invalid business reference metadata.");
            var marker = uri.AbsolutePath.IndexOf("/entity/", StringComparison.Ordinal);
            if (marker < 0) throw new VerifierException("Unrecognized business reference resource.");
            var key = uri.AbsolutePath[(marker + 8)..].TrimEnd('/');
            var segments = key.Split('/');
            if (segments.Length == 2 && DocumentRegistry.All.Any(x => x.EntityType == type && x.TransferMode == DocumentTransferMode.Recreate))
            {
                if (segments[0] != type || !Guid.TryParse(segments[1], out var id))
                    throw new VerifierException("Invalid recreated document reference.");
                output["$ref"] = new JsonObject { ["type"] = type, ["semanticHash"] = await resolver(type, id, ct) };
            }
            else output["$ref"] = new JsonObject { ["type"] = type, ["key"] = key };
        }
        foreach (var (key, value) in obj)
        {
            if (key == "meta") continue;
            output[key] = await NormalizeValue(value, resolver, ct);
        }
        return output;
    }
}

public sealed class SalesReturnNormalizer(DocumentRule rule) : DocumentNormalizer(rule);
public sealed class PurchaseReturnNormalizer(DocumentRule rule) : DocumentNormalizer(rule);
public sealed class RetailSalesReturnNormalizer(DocumentRule rule) : DocumentNormalizer(rule);
