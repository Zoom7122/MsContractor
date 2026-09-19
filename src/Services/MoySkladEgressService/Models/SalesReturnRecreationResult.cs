namespace MsContractor.MoySkladEgressService.Models;

public sealed record SalesReturnRecreationResult(
    Guid OperationId,
    Guid MainAgentId,
    IReadOnlyList<SalesReturnRecreationDocumentResult> Documents);

public sealed record SalesReturnRecreationDocumentResult(
    Guid SourceDocumentId,
    Guid? NewDocumentId,
    Guid? RollbackDocumentId,
    string Stage,
    string Status,
    string? ErrorCode,
    string? Error);
