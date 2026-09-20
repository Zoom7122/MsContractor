namespace MsContractor.MoySkladEgressService.Models;

public sealed record SalesReturnRecreationResult(
    Guid OperationId,
    Guid MainAgentId,
    IReadOnlyList<SalesReturnRecreationDocumentResult> Documents);

public sealed record SalesReturnRecreationDocumentResult(
    Guid SourceDocumentId,
    Guid? NewDocumentId,
    string Stage,
    string Status,
    string? ErrorCode,
    string? Error);
