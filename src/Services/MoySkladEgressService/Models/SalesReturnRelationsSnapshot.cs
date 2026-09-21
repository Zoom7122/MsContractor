using System.Text.Json;

namespace MsContractor.MoySkladEgressService.Models;

public sealed class SalesReturnRelationsSnapshot
{
    public Guid SourceSalesReturnId { get; init; }

    public List<PaymentOutRelationSnapshot> PaymentOuts { get; init; } = [];

    public List<CashOutRelationSnapshot> CashOuts { get; init; } = [];

    public List<LossRelationSnapshot> Losses { get; init; } = [];
}

public sealed class PaymentOutRelationSnapshot
{
    public Guid DocumentId { get; init; }

    public JsonElement[] OperationsBefore { get; init; } = [];

    public decimal LinkedSum { get; init; }
}

public sealed class CashOutRelationSnapshot
{
    public Guid DocumentId { get; init; }

    public JsonElement[] OperationsBefore { get; init; } = [];

    public decimal LinkedSum { get; init; }
}

public sealed class LossRelationSnapshot
{
    public Guid DocumentId { get; init; }

    public JsonElement? SalesReturnBefore { get; init; }
}

public enum SalesReturnRelationDocumentType
{
    PaymentOut,
    CashOut,
    Loss
}
