namespace MsContractor.MoySkladEgressService.Models;

public sealed record FactureOutPayload(
    Guid SourceDocumentId,
    string PayloadJson)
{
    public Guid NewSyncId { get; init; }
}

public sealed record FactureOutPayloadBuildResult(
    IReadOnlyList<FactureOutPayload> Payloads,
    IReadOnlyList<FactureOutSkippedDocumentResult> SkippedDocuments);
