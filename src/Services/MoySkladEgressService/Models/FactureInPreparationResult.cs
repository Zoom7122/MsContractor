namespace MsContractor.MoySkladEgressService.Models;

public sealed record FactureInPreparationResult(
    IReadOnlyDictionary<Guid, string> Documents,
    IReadOnlyList<FactureInSkippedDocumentResult> SkippedDocuments);
