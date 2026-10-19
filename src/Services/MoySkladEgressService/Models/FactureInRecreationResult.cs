namespace MsContractor.MoySkladEgressService.Models;

public sealed record FactureInRecreationResult(
    IReadOnlyList<Guid> TransferredDocumentIds,
    IReadOnlyList<FactureInSkippedDocumentResult> SkippedDocuments,
    IReadOnlyList<FactureInCreatedWithErrorResult> CreatedWithErrors,
    IReadOnlyList<FactureInFailedDocumentResult> FailedDocuments)
{
    public IReadOnlyList<DocumentRecreationTransferredDocumentResult> TransferredDocuments { get; init; } = [];
    public IReadOnlyList<DocumentRecreationUnCreatedButDeletedResult> UnCreatedButDeleted { get; init; } = [];
}

public sealed record FactureInSkippedDocumentResult(
    Guid DocumentId,
    string Status,
    string? ErrorCode,
    string? Reason,
    Exception? Exception = null);

public sealed record FactureInCreatedWithErrorResult(
    Guid SourceDocumentId,
    Guid NewDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);

public sealed record FactureInFailedDocumentResult(
    Guid SourceDocumentId,
    Guid NewSyncId,
    Guid? NewDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);
