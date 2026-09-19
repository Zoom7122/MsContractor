namespace MsContractor.MoySkladEgressService.Models;

public sealed class SalesReturnRecreationOperation
{
    public Guid AccountId { get; set; }

    public Guid OperationId { get; set; }

    public Guid MainAgentId { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SalesReturnRecreationItem> Items { get; set; } = [];
}

public sealed class SalesReturnRecreationItem
{
    public Guid AccountId { get; set; }

    public Guid OperationId { get; set; }

    public Guid SourceDocumentId { get; set; }

    public Guid? DemandId { get; set; }

    public Guid? TargetAgentAccountId { get; set; }

    public Guid NewSyncId { get; set; }

    public Guid RollbackSyncId { get; set; }

    public Guid? NewDocumentId { get; set; }

    public Guid? RollbackDocumentId { get; set; }

    public string Stage { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public string SourceRawJson { get; set; } = string.Empty;

    public string NewPayloadJson { get; set; } = string.Empty;

    public string RollbackPayloadJson { get; set; } = string.Empty;
}
