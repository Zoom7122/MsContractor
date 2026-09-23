namespace MsContractor.MoySkladEgressService.Models;

public sealed class PurchaseReturnFactureOutRawData
{
    public Guid AccountId { get; set; }

    public Guid PurchaseReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string RawJson { get; set; } = string.Empty;
}

public sealed class PurchaseReturnFactureInRawData
{
    public Guid AccountId { get; set; }

    public Guid PurchaseReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string RawJson { get; set; } = string.Empty;
}

public sealed class PurchaseReturnPaymentInRawData
{
    public Guid AccountId { get; set; }

    public Guid PurchaseReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string RawJson { get; set; } = string.Empty;

    public string? OperationsBeforeJson { get; set; }

    public decimal? LinkedSum { get; set; }
}

public sealed class PurchaseReturnCashInRawData
{
    public Guid AccountId { get; set; }

    public Guid PurchaseReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string RawJson { get; set; } = string.Empty;

    public string? OperationsBeforeJson { get; set; }

    public decimal? LinkedSum { get; set; }
}
