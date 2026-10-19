using System.Text.Json.Serialization;

namespace MsContractor.MoySkladEgressService.Models;

public sealed record SalesReturnRecreationResult(
    IReadOnlyList<Guid> TransferredDocumentIds,
    IReadOnlyList<Guid> SkippedDocumentIds,
    IReadOnlyList<SalesReturnSkippedDocumentResult> SkippedDocuments,
    IReadOnlyList<SalesReturnCreatedWithErrorResult> CreatedWithErrors,
    IReadOnlyList<SalesReturnUnCreatedButDeletedResult> UnCreatedButDeleted)
{
    [JsonIgnore]
    public Guid OperationId { get; init; }
}

public sealed record SalesReturnSkippedDocumentResult(
    Guid DocumentId,
    string Status,
    string? ErrorCode,
    string? Error);

public sealed record SalesReturnCreatedWithErrorResult(
    Guid SourceDocumentId,
    Guid NewDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);

public sealed record SalesReturnUnCreatedButDeletedResult(
    Guid SourceDocumentId,
    string Status,
    string? ErrorCode,
    string? Error);
