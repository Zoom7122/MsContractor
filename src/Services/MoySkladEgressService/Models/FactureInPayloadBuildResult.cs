namespace MsContractor.MoySkladEgressService.Models;

public sealed record FactureInPayloadBuildResult(
    IReadOnlyList<FactureInRecreationItem> PreparedDocuments,
    IReadOnlyList<FactureInSkippedDocumentResult> SkippedDocuments,
    IReadOnlyList<FactureInRecreationItem> CompletedDocuments,
    IReadOnlyList<FactureInFailedDocumentResult> FailedDocuments);
