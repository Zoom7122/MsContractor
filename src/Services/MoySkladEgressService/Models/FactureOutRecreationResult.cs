namespace MsContractor.MoySkladEgressService.Models;

public sealed record FactureOutRecreationResult(
    IReadOnlyList<Guid> TransferredDocumentIds,
    IReadOnlyList<FactureOutSkippedDocumentResult> SkippedDocuments,
    IReadOnlyList<FactureOutCreatedWithErrorResult> CreatedWithErrors,
    IReadOnlyList<FactureOutFailedDocumentResult> FailedDocuments);

public sealed record FactureOutSkippedDocumentResult(
    Guid DocumentId,
    string Status,
    string? ErrorCode,
    string? Reason,
    Exception? Exception = null);

public sealed record FactureOutCreatedWithErrorResult(
    Guid SourceDocumentId,
    Guid NewDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);

public sealed record FactureOutFailedDocumentResult(
    Guid SourceDocumentId,
    Guid NewSyncId,
    Guid? NewDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);
