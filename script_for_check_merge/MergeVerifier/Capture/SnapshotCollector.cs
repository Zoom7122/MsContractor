using System.Text.Json;
using System.Text.Json.Nodes;
using MergeVerifier.Documents;
using MergeVerifier.Models;
using MergeVerifier.MoySklad;
using MergeVerifier.Normalization;

namespace MergeVerifier.Capture;

public sealed class SnapshotCollector(IMoySkladClient client, TextWriter progress)
{
    private readonly Dictionary<(string, Guid), JsonObject> raw = [];
    private readonly Dictionary<(string, Guid), JsonObject> normalized = [];
    private readonly HashSet<(string, Guid)> active = [];

    public async Task<MergeSnapshot> CaptureAsync(Guid main, IReadOnlyList<Guid> duplicates, CancellationToken ct)
    {
        raw.Clear(); normalized.Clear(); active.Clear();
        var started = DateTimeOffset.UtcNow;
        var owners = new[] { main }.Concat(duplicates).ToArray();
        var documents = new List<(DocumentRule Rule, Guid Id, Guid Agent)>();
        var coverage = new List<CaptureCoverage>();
        var seen = new HashSet<(string, Guid)>();
        foreach (var rule in DocumentRegistry.All)
        {
            progress.WriteLine($"[{rule.EntityType}]");
            if (!rule.AgentFilterSupported)
            {
                progress.WriteLine("UNSUPPORTED: no documented agent filter; account-wide scan skipped.");
                coverage.Add(new(rule.EntityType, false, "Agent filtering is not documented; type is unsupported."));
                continue;
            }
            foreach (var owner in owners)
            {
                // `filter` is the query parameter name and the `=` operator. Encode only
                // the reference value. Encoding the complete expression (`agent%3D...`)
                // makes MoySklad parse it as a field-less filter and return HTTP 400.
                var agentReference = new Uri(client.BaseUrl, $"entity/counterparty/{owner:D}");
                var filter = "agent=" + Uri.EscapeDataString(agentReference.AbsoluteUri);
                var rows = await client.GetAllAsync($"entity/{rule.EntityType}?filter={filter}", ct);
                foreach (var row in rows)
                {
                    var id = ReadId(row);
                    if (!seen.Add((rule.EntityType, id))) throw new VerifierException("Document returned more than once across capture scope.");
                    if (ReadAgent(row) != owner) throw new VerifierException("API agent filter returned a document outside requested scope.");
                    var detail = await LoadDocument(rule, id, ct);
                    if (ReadAgent(detail) != owner) throw new VerifierException("Document owner changed during capture; repeat capture.");
                    documents.Add((rule, id, owner));
                }
                progress.WriteLine($"{(owner == main ? "main" : $"duplicate {Array.IndexOf(owners, owner)}"),-20} {rows.Count}");
            }
            coverage.Add(new(rule.EntityType, true));
        }
        var snapshots = new List<DocumentSnapshot>();
        foreach (var (rule, id, agent) in documents)
        {
            snapshots.Add(new()
            {
                EntityType = rule.EntityType, TransferMode = rule.TransferMode, SourceCounterpartyId = agent,
                StableDocumentId = rule.TransferMode == DocumentTransferMode.Recreate ? null : id.ToString("D"),
                Data = JsonSerializer.SerializeToElement(await Normalize(rule.EntityType, id, ct))
            });
        }
        // Guard against accidental leaks through unexplained technical/self references.
        // A business field that literally embeds such an ID cannot be silently discarded to obtain PASS.
        var recreatedIds = raw.Keys.Where(x => DocumentRegistry.Get(x.Item1).TransferMode == DocumentTransferMode.Recreate)
            .Select(x => x.Item2.ToString("D")).ToArray();
        foreach (var snapshot in snapshots)
            if (ContainsUnexplainedIdentity(snapshot.Data, recreatedIds))
                throw new VerifierException("Recreated document ID remains in normalized data; a semantic rule is required.");
        return new()
        {
            MainCounterpartyId = main, DuplicateCounterpartyIds = duplicates,
            CaptureStartedAt = started, CaptureCompletedAt = DateTimeOffset.UtcNow,
            Coverage = coverage, Documents = snapshots
        };
    }

    private static bool ContainsUnexplainedIdentity(JsonElement data, string[] ids)
    {
        if (data.ValueKind == JsonValueKind.String)
            return ids.Any(id => data.GetString()!.Contains(id, StringComparison.OrdinalIgnoreCase));
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().Any(x => ContainsUnexplainedIdentity(x, ids));
        if (data.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in data.EnumerateObject())
        {
            // Identity is scoped by entity type. A product/file can legitimately have the same UUID.
            if (property.Name == "$ref" && property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("type", out var type) &&
                !DocumentRegistry.All.Any(x => x.EntityType == type.GetString() && x.TransferMode == DocumentTransferMode.Recreate)) continue;
            if (property.Name == "fileIdentity") continue;
            if (ContainsUnexplainedIdentity(property.Value, ids)) return true;
        }
        return false;
    }

    private async Task<JsonObject> LoadDocument(DocumentRule rule, Guid id, CancellationToken ct)
    {
        if (raw.TryGetValue((rule.EntityType, id), out var cached)) return cached;
        var document = await client.GetAsync($"entity/{rule.EntityType}/{id:D}", ct);
        if (ReadId(document) != id || document["meta"]?["type"]?.GetValue<string>() != rule.EntityType)
            throw new VerifierException("Document detail identity does not match requested resource.");
        foreach (var collection in rule.PositionCollections)
        {
            var rows = await client.GetAllAsync($"entity/{rule.EntityType}/{id:D}/{collection.ToLowerInvariant()}", ct);
            CheckCollectionSize(document[collection], rows.Count);
            document[collection] = new JsonArray(rows.Select(x => (JsonNode)x.DeepClone()).ToArray());
        }
        await ExpandCollections(document, ct);
        raw[(rule.EntityType, id)] = document;
        return document;
    }

    private async Task ExpandCollections(JsonNode? node, CancellationToken ct, int depth = 0)
    {
        if (depth > 32) throw new VerifierException("Nested collection depth exceeded.");
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (value is JsonObject child && child["meta"] is JsonObject meta && meta.ContainsKey("size"))
                {
                    var href = meta["href"]?.GetValue<string>() ?? throw new VerifierException("Collection URL is missing.");
                    var rows = await client.GetAllAsync(href, ct);
                    CheckCollectionSize(child, rows.Count);
                    obj[key] = new JsonArray(rows.Select(x => (JsonNode)x.DeepClone()).ToArray());
                }
                if (key != "meta") await ExpandCollections(obj[key], ct, depth + 1);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) await ExpandCollections(child, ct, depth + 1);
    }

    private static void CheckCollectionSize(JsonNode? original, int actual)
    {
        if (original is JsonObject obj && obj["meta"]?["size"] is { } size && size.GetValue<int>() != actual)
            throw new VerifierException("Document collection size changed during capture; repeat capture.");
    }

    private async Task<JsonObject> Normalize(string type, Guid id, CancellationToken ct)
    {
        var key = (type, id);
        if (normalized.TryGetValue(key, out var cached)) return cached;
        if (!active.Add(key)) throw new VerifierException("Cyclic recreated document references need an explicit semantic rule.");
        try
        {
            var rule = DocumentRegistry.Get(type);
            var doc = await LoadDocument(rule, id, ct);
            var data = await DocumentNormalizer.For(rule).NormalizeAsync(doc,
                async (relatedType, relatedId, token) => SemanticHasher.Hash(await Normalize(relatedType, relatedId, token)), ct);
            normalized[key] = data;
            return data;
        }
        finally { active.Remove(key); }
    }

    public static Guid ReadId(JsonObject document) => Guid.TryParse(document["id"]?.GetValue<string>(), out var id) && id != Guid.Empty
        ? id : throw new VerifierException("Document has an invalid or missing UUID.");

    public static Guid ReadAgent(JsonObject document)
    {
        var agent = document["agent"] as JsonObject;
        var href = agent?["meta"]?["href"]?.GetValue<string>();
        if (agent?["meta"]?["type"]?.GetValue<string>() != "counterparty" ||
            !Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.Contains("/entity/counterparty/", StringComparison.Ordinal) ||
            !Guid.TryParse(uri.Segments.Last().Trim('/'), out var id) || id == Guid.Empty)
            throw new VerifierException("Document has no verifiable counterparty agent reference.");
        return id;
    }
}
