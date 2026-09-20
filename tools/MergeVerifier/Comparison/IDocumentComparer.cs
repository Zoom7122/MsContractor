using MergeVerifier.Models;

namespace MergeVerifier.Comparison;

public interface IDocumentComparer
{
    bool CanCompare(string documentType);
    DocumentVerification Compare(DocumentSnapshot before, IReadOnlyList<DocumentSnapshot> after,
        Guid mainCounterpartyId, out DocumentSnapshot? matched);
}

public sealed class ReassignedDocumentComparer : IDocumentComparer
{
    public bool CanCompare(string documentType) => !string.Equals(documentType, "salesreturn", StringComparison.Ordinal);

    public DocumentVerification Compare(DocumentSnapshot before, IReadOnlyList<DocumentSnapshot> after,
        Guid mainCounterpartyId, out DocumentSnapshot? matched)
    {
        matched = after.SingleOrDefault(item => item.DocumentType == before.DocumentType && item.DocumentId == before.DocumentId);
        if (matched is null)
            return new DocumentVerification(before.DocumentType, before.DocumentId, null, "reassigned", "Missing");
        if (matched.AgentId != mainCounterpartyId)
            return new DocumentVerification(before.DocumentType, before.DocumentId, matched.DocumentId, "reassigned", "Changed",
                $"agent is {matched.AgentId:D}, expected {mainCounterpartyId:D}");
        var expected = Normalization.DocumentNormalizer.Normalize(before.DocumentType, before.RawJson, before.PositionRawJson, false).Hash;
        var actual = Normalization.DocumentNormalizer.Normalize(matched.DocumentType, matched.RawJson, matched.PositionRawJson, false).Hash;
        return expected == actual
            ? new DocumentVerification(before.DocumentType, before.DocumentId, matched.DocumentId, "reassigned", "Matched")
            : new DocumentVerification(before.DocumentType, before.DocumentId, matched.DocumentId, "reassigned", "Changed",
                "Business content or positions changed.");
    }
}

public class RecreatedDocumentComparer(string documentType) : IDocumentComparer
{
    public bool CanCompare(string type) => string.Equals(type, documentType, StringComparison.Ordinal);

    public DocumentVerification Compare(DocumentSnapshot before, IReadOnlyList<DocumentSnapshot> after,
        Guid mainCounterpartyId, out DocumentSnapshot? matched)
    {
        var candidates = after.Where(item => item.DocumentType == before.DocumentType && item.AgentId == mainCounterpartyId &&
            item.NormalizedHash == before.NormalizedHash).ToArray();
        if (candidates.Length == 1)
        {
            matched = candidates[0];
            return new DocumentVerification(before.DocumentType, before.DocumentId, matched.DocumentId, "recreated", "Matched");
        }
        matched = null;
        if (candidates.Length > 1)
            return new DocumentVerification(before.DocumentType, before.DocumentId, null, "recreated", "Ambiguous",
                "More than one semantically equivalent document was found.");
        var similar = after.Where(item => item.DocumentType == before.DocumentType && item.AgentId == mainCounterpartyId &&
            SameIdentity(before, item)).ToArray();
        if (similar.Length == 1)
        {
            matched = similar[0];
            return new DocumentVerification(before.DocumentType, before.DocumentId, matched.DocumentId, "recreated", "Changed",
                "A likely recreated document has different business content or positions.");
        }
        return new DocumentVerification(before.DocumentType, before.DocumentId, null, "recreated", "Missing",
            "No equivalent recreated document was found.");
    }

    private static bool SameIdentity(DocumentSnapshot left, DocumentSnapshot right) =>
        (!string.IsNullOrWhiteSpace(left.ExternalCode) && left.ExternalCode == right.ExternalCode) ||
        (!string.IsNullOrWhiteSpace(left.Name) && left.Name == right.Name && left.Moment == right.Moment);
}

public sealed class SalesReturnComparer() : RecreatedDocumentComparer("salesreturn");
// Kept as explicit extension points if these types acquire recreate flows in Egress.
public sealed class PurchaseReturnComparer() : RecreatedDocumentComparer("purchasereturn");
public sealed class RetailSalesReturnComparer() : RecreatedDocumentComparer("retailsalesreturn");
public sealed class FactureOutComparer() : RecreatedDocumentComparer("factureout");
public sealed class FactureInComparer() : RecreatedDocumentComparer("facturein");
