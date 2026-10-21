namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladDocumentChangeChunkResult(
    IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeItem> ChangedDocuments,
    IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeFailure> Failures)
{
    public IReadOnlyList<MsContractor.Contracts.Internal.MoySkladDocumentChangeSkippedItem> SkippedDocuments { get; init; } = [];
}
