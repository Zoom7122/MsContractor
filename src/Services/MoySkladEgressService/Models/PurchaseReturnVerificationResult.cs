namespace MsContractor.MoySkladEgressService.Models;

public sealed record PurchaseReturnVerificationResult(
    IReadOnlyList<PurchaseReturnDocumentVerificationResult> Documents)
{
    public IReadOnlyList<RecreatedPurchaseReturn> RecreatedPurchaseReturns { get; init; } = [];
    public IReadOnlyList<RecreatedPurchaseReturnFactureOut> RecreatedFactureOuts { get; init; } = [];
    public IReadOnlyList<FactureOutSkippedDocumentResult> SkippedFactureOuts { get; init; } = [];
    public IReadOnlyList<DocumentRecreationUnCreatedButDeletedResult> UnCreatedButDeleted { get; init; } = [];

    public bool Passed => Documents.All(document => document.Status == "Verified");
}

public sealed record PurchaseReturnDocumentVerificationResult(
    Guid SourceDocumentId,
    Guid? NewDocumentId,
    string Status,
    IReadOnlyList<PurchaseReturnFieldMismatch> FieldMismatches,
    IReadOnlyList<PurchaseReturnPositionMismatch> PositionMismatches,
    IReadOnlyList<string> Warnings,
    string? ErrorCode = null,
    string? Error = null);

public sealed record PurchaseReturnFieldMismatch(
    string Field,
    string? Expected,
    string? Actual);

public sealed record PurchaseReturnPositionMismatch(
    string Kind,
    string? Expected,
    string? Actual);

public sealed record RecreatedPurchaseReturn(Guid SourceDocumentId, Guid NewDocumentId);

public sealed record RecreatedPurchaseReturnFactureOut(
    Guid SourcePurchaseReturnId,
    Guid SourceDocumentId,
    Guid NewDocumentId);
