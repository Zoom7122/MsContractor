namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladDocumentChangeChunkResult(
    IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeItem> ChangedDocuments,
    IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeFailure> Failures);
