namespace MsContractor.MoySkladEgressService.Models;

public sealed record PurchaseReturnPreparationResult(
    IReadOnlyList<Guid> ReadyForRecreationIds,
    IReadOnlyList<PurchaseReturnSkippedDocument> Skipped);

public sealed record PurchaseReturnSkippedDocument(
    Guid PurchaseReturnId,
    string Reason);
