namespace MsContractor.MoySkladEgressService.Models;

public sealed record FactureOutPreparationResult(
    IReadOnlyList<Guid> DocumentIds,
    IReadOnlyList<FactureOutSkippedDocumentResult> SkippedDocuments);
