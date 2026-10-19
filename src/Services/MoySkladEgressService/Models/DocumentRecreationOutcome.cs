namespace MsContractor.MoySkladEgressService.Models;

public sealed record DocumentRecreationTransferredDocumentResult(Guid SourceDocumentId, Guid NewDocumentId);

public sealed record DocumentRecreationUnCreatedButDeletedResult(
    Guid SourceDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);
