using System.Text.Json;
using System.Text.Json.Nodes;
using MergeVerifier.Documents;
using MergeVerifier.Models;
using MergeVerifier.Normalization;

namespace MergeVerifier.Comparison;

/// <summary>
/// A reference to a recreated document carries the full semantic hash of that document. When only the linked
/// document changed, every referencing document would otherwise be reported as changed too. The linked view drops
/// <c>semanticHash</c> from references that keep their <c>linkKey</c> (same counterpart before and after); the linked
/// document itself is still compared in full under its own type.
/// </summary>
public static class LinkedView
{
    public static JsonElement Of(JsonElement data) => JsonSerializer.SerializeToElement(Strip(JsonNode.Parse(data.GetRawText())));

    private static JsonNode? Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonObject reference && reference.ContainsKey("linkKey")) reference.Remove("semanticHash");
                foreach (var (_, value) in obj.ToArray()) Strip(value);
                break;
            case JsonArray array:
                foreach (var item in array) Strip(item);
                break;
        }
        return node;
    }
}

public interface IDocumentComparer
{
    string EntityType { get; }
    TypeComparisonResult Compare(IReadOnlyList<DocumentSnapshot> before, IReadOnlyList<DocumentSnapshot> after, Guid main);
}

public sealed class PutAgentDocumentComparer(DocumentRule rule) : IDocumentComparer
{
    public string EntityType => rule.EntityType;
    public TypeComparisonResult Compare(IReadOnlyList<DocumentSnapshot> before, IReadOnlyList<DocumentSnapshot> after, Guid main)
    {
        var results = new List<DocumentComparisonResult>();
        var mainDocuments = after.Where(x => x.SourceCounterpartyId == main).ToDictionary(x => x.StableDocumentId!);
        var beforeIds = before.Select(x => x.StableDocumentId!).ToHashSet();
        foreach (var doc in before)
        {
            if (!mainDocuments.TryGetValue(doc.StableDocumentId!, out var target))
                results.Add(Result(VerificationStatus.Missing, doc, StructuredDiff.Compare(doc.Data, null)));
            else if (StructuredDiff.Compare(doc.Data, target.Data).Count == 0)
                results.Add(Result(VerificationStatus.Matched, doc));
            else
            {
                // Differences that remain after removing linked-document hashes belong to this document.
                var own = StructuredDiff.Compare(LinkedView.Of(doc.Data), LinkedView.Of(target.Data));
                results.Add(own.Count == 0
                    ? Result(VerificationStatus.Matched, doc) with
                    {
                        Note = "Only linked recreated documents differ; they are reported under their own types."
                    }
                    : Result(VerificationStatus.Changed, doc, own));
            }
        }
        foreach (var doc in mainDocuments.Values.Where(x => !beforeIds.Contains(x.StableDocumentId!)))
            results.Add(Result(VerificationStatus.Unexpected, doc, StructuredDiff.Compare(null, doc.Data)));
        foreach (var doc in after.Where(x => x.SourceCounterpartyId != main))
            results.Add(Result(VerificationStatus.StillOnDuplicate, doc));
        return new(EntityType, rule.TransferMode, before.Count, mainDocuments.Count, results);
    }

    private static DocumentComparisonResult Result(VerificationStatus status, DocumentSnapshot doc, IReadOnlyList<FieldDifference>? diff = null) => new()
    {
        Status = status, StableDocumentId = doc.StableDocumentId, SourceCounterpartyId = doc.SourceCounterpartyId,
        Data = doc.Data, Differences = diff ?? []
    };
}

public class RecreatedDocumentComparer(DocumentRule rule) : IDocumentComparer
{
    public string EntityType => rule.EntityType;

    public TypeComparisonResult Compare(IReadOnlyList<DocumentSnapshot> before, IReadOnlyList<DocumentSnapshot> after, Guid main)
    {
        var mainDocuments = after.Where(x => x.SourceCounterpartyId == main).ToArray();
        // Canonical keys additionally avoid treating a theoretical hash collision as a match.
        // Grouped by the linked view: a change of a linked recreated document is reported under that document only.
        var left = before.GroupBy(Key).ToDictionary(x => x.Key, x => x.ToArray());
        var right = mainDocuments.GroupBy(Key).ToDictionary(x => x.Key, x => x.ToArray());
        var results = new List<DocumentComparisonResult>();
        foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            var source = left.GetValueOrDefault(key) ?? [];
            var target = right.GetValueOrDefault(key) ?? [];
            var matched = Math.Min(source.Length, target.Length);
            if (matched > 0) results.Add(Result(VerificationStatus.Matched, source[0], matched));
            if (source.Length > matched)
                results.Add(Result(VerificationStatus.Missing, source[0], source.Length - matched));
            if (target.Length > matched)
                results.Add(Result(VerificationStatus.Unexpected, target[0], target.Length - matched));
        }
        foreach (var group in after.Where(x => x.SourceCounterpartyId != main)
                     .GroupBy(x => (x.SourceCounterpartyId, Canonical: Key(x))))
            results.Add(Result(VerificationStatus.StillOnDuplicate, group.First(), group.Count()));
        // A sole unmatched pair is a useful diagnostic candidate, never an inferred oldId -> newId mapping.
        var missing = results.Where(x => x.Status == VerificationStatus.Missing).ToArray();
        var unexpected = results.Where(x => x.Status == VerificationStatus.Unexpected).ToArray();
        if (missing.Length == 1 && unexpected.Length == 1)
        {
            var index = results.IndexOf(missing[0]);
            results[index] = missing[0] with
            {
                Differences = StructuredDiff.Compare(LinkedView.Of(missing[0].Data!.Value), LinkedView.Of(unexpected[0].Data!.Value)),
                Note = "Difference against the sole unexpected semantic variant; diagnostic candidate, not an identity match."
            };
        }
        return new(EntityType, rule.TransferMode, before.Count, mainDocuments.Length, results);
    }

    private static string Key(DocumentSnapshot document) => JsonCanonicalizer.Canonicalize(LinkedView.Of(document.Data));

    private static DocumentComparisonResult Result(VerificationStatus status, DocumentSnapshot doc, int count) => new()
    {
        Status = status, Count = count, SemanticHash = SemanticHasher.Hash(doc.Data), Data = doc.Data,
        SourceCounterpartyId = doc.SourceCounterpartyId,
        Differences = status == VerificationStatus.Missing ? StructuredDiff.Compare(doc.Data, null)
            : status == VerificationStatus.Unexpected ? StructuredDiff.Compare(null, doc.Data) : []
    };
}

public sealed class SalesReturnComparer(DocumentRule rule) : RecreatedDocumentComparer(rule);
public sealed class PurchaseReturnComparer(DocumentRule rule) : RecreatedDocumentComparer(rule);
public sealed class RetailSalesReturnComparer(DocumentRule rule) : RecreatedDocumentComparer(rule);
public sealed class FactureInComparer(DocumentRule rule) : RecreatedDocumentComparer(rule);
public sealed class FactureOutComparer(DocumentRule rule) : RecreatedDocumentComparer(rule);

public sealed class UnsupportedDocumentComparer(DocumentRule rule) : IDocumentComparer
{
    public string EntityType => rule.EntityType;
    public TypeComparisonResult Compare(IReadOnlyList<DocumentSnapshot> before, IReadOnlyList<DocumentSnapshot> after, Guid main) =>
        new(EntityType, DocumentTransferMode.Unsupported, before.Count, after.Count(x => x.SourceCounterpartyId == main),
        [new() { Status = VerificationStatus.Unsupported, Note = "UNSUPPORTED DOCUMENT TYPE: " + EntityType }]);
}

public static class DocumentComparisonService
{
    public static IDocumentComparer For(DocumentRule rule) => rule.TransferMode switch
    {
        DocumentTransferMode.PutAgent => new PutAgentDocumentComparer(rule),
        DocumentTransferMode.Recreate => rule.EntityType switch
        {
            "salesreturn" => new SalesReturnComparer(rule),
            "purchasereturn" => new PurchaseReturnComparer(rule),
            "retailsalesreturn" => new RetailSalesReturnComparer(rule),
            "facturein" => new FactureInComparer(rule),
            "factureout" => new FactureOutComparer(rule),
            _ => throw new VerifierException("No comparer for recreated document type.")
        },
        _ => new UnsupportedDocumentComparer(rule)
    };

    public static VerificationReport Compare(MergeSnapshot before, MergeSnapshot after)
    {
        Storage.SnapshotStore.Validate(before);
        Storage.SnapshotStore.Validate(after);
        if (before.MainCounterpartyId != after.MainCounterpartyId ||
            !before.DuplicateCounterpartyIds.Order().SequenceEqual(after.DuplicateCounterpartyIds.Order()))
            throw new VerifierException("Snapshot scopes do not match.");
        return new()
        {
            GeneratedAt = DateTimeOffset.UtcNow, MainCounterpartyId = before.MainCounterpartyId,
            Types = DocumentRegistry.All.Select(rule => For(rule).Compare(
                before.Documents.Where(x => x.EntityType == rule.EntityType).ToArray(),
                after.Documents.Where(x => x.EntityType == rule.EntityType).ToArray(), before.MainCounterpartyId)).ToArray()
        };
    }
}
