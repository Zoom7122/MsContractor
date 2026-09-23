namespace MsContractor.MoySkladEgressService.Models;

public sealed record PurchaseReturnFactureRelationsSnapshot
{
    public PurchaseReturnFactureRelationsSnapshot(
        Guid purchaseReturnId,
        IReadOnlyList<string> factureIn,
        IReadOnlyList<string> factureOut)
        : this(purchaseReturnId, factureIn, factureOut, [], [])
    {
    }

    public PurchaseReturnFactureRelationsSnapshot(
        Guid purchaseReturnId,
        IReadOnlyList<string> factureIn,
        IReadOnlyList<string> factureOut,
        IReadOnlyList<PurchaseReturnMoneyRelationSnapshot> paymentIns,
        IReadOnlyList<PurchaseReturnMoneyRelationSnapshot> cashIns)
    {
        PurchaseReturnId = purchaseReturnId;
        FactureIn = factureIn;
        FactureOut = factureOut;
        PaymentIns = paymentIns;
        CashIns = cashIns;
    }

    public Guid PurchaseReturnId { get; }

    public IReadOnlyList<string> FactureIn { get; }

    public IReadOnlyList<string> FactureOut { get; }

    public IReadOnlyList<PurchaseReturnMoneyRelationSnapshot> PaymentIns { get; }

    public IReadOnlyList<PurchaseReturnMoneyRelationSnapshot> CashIns { get; }
}

public sealed record PurchaseReturnMoneyRelationSnapshot(
    Guid DocumentId,
    string RawJson,
    string? OperationsBeforeJson = null,
    decimal? LinkedSum = null);

public sealed record PurchaseReturnMoneyRelationSnapshotUpdate(
    Guid PurchaseReturnId,
    Guid DocumentId,
    bool IsCashIn,
    string OperationsBeforeJson,
    decimal LinkedSum);

public sealed record PurchaseReturnFactureRelationResult(
    Guid PurchaseReturnId,
    string Status,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool IsReadyForRecreation =>
        string.Equals(Status, "Detached", StringComparison.Ordinal) ||
        string.Equals(Status, "NoRelations", StringComparison.Ordinal);
}

public sealed record PurchaseReturnFactureRelationsResult(
    IReadOnlyList<PurchaseReturnFactureRelationResult> Documents)
{
    public IReadOnlyList<Guid> ReadyForRecreationIds =>
        Documents
            .Where(item => item.IsReadyForRecreation)
            .Select(item => item.PurchaseReturnId)
            .ToArray();

    public IReadOnlyList<PurchaseReturnFactureRelationResult> Failed =>
        Documents
            .Where(item => !item.IsReadyForRecreation)
            .ToArray();
}

public sealed record PurchaseReturnRelationsReattachResult(
    IReadOnlyList<PurchaseReturnRelationsReattachItem> Documents)
{
    public IReadOnlyList<PurchaseReturnRelationsReattachItem> Failed =>
        Documents
            .Where(item => !item.IsSuccessful)
            .ToArray();

    public IReadOnlyList<Guid> ReadyForVerificationIds =>
        Documents
            .Where(item => item.IsSuccessful)
            .Select(item => item.PurchaseReturnId)
            .ToArray();
}

public sealed record PurchaseReturnRelationsReattachItem(
    Guid PurchaseReturnId,
    Guid NewPurchaseReturnId,
    string Status,
    string? ErrorCode = null,
    string? Error = null)
{
    public bool IsSuccessful =>
        string.Equals(Status, "Reattached", StringComparison.Ordinal) ||
        string.Equals(Status, "NoRelations", StringComparison.Ordinal);
}
