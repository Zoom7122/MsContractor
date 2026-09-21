namespace MsContractor.MoySkladEgressService.Models;

public sealed class SalesReturnRelationsSnapshotRecord
{
    public Guid AccountId { get; set; }

    public Guid OperationId { get; set; }

    public Guid SourceSalesReturnId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public List<PaymentOutRelationSnapshotRecord> PaymentOuts { get; set; } = [];

    public List<CashOutRelationSnapshotRecord> CashOuts { get; set; } = [];

    public List<LossRelationSnapshotRecord> Losses { get; set; } = [];
}

public sealed class PaymentOutRelationSnapshotRecord
{
    public Guid AccountId { get; set; }

    public Guid OperationId { get; set; }

    public Guid SourceSalesReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string OperationsBeforeJson { get; set; } = "[]";

    public decimal LinkedSum { get; set; }

    public string DetachStatus { get; set; } = "Prepared";

    public string ReattachStatus { get; set; } = "Pending";

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }
}

public sealed class CashOutRelationSnapshotRecord
{
    public Guid AccountId { get; set; }

    public Guid OperationId { get; set; }

    public Guid SourceSalesReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string OperationsBeforeJson { get; set; } = "[]";

    public decimal LinkedSum { get; set; }

    public string DetachStatus { get; set; } = "Prepared";

    public string ReattachStatus { get; set; } = "Pending";

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }
}

public sealed class LossRelationSnapshotRecord
{
    public Guid AccountId { get; set; }

    public Guid OperationId { get; set; }

    public Guid SourceSalesReturnId { get; set; }

    public Guid DocumentId { get; set; }

    public string? SalesReturnBeforeJson { get; set; }

    public string DetachStatus { get; set; } = "Prepared";

    public string ReattachStatus { get; set; } = "Pending";

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }
}
