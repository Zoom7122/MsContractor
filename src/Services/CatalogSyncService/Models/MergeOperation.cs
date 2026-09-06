namespace MsContractor.CatalogSyncService.Models;

public static class MergeOperationTypes
{
    public const string DiscoverDocuments = "discover_documents";
    public const string UpdateMainCounterparty = "update_main_counterparty";
    public const string ChangeDocumentCounterparties = "change_document_counterparties";
    public const string ArchiveDuplicate = "archive_duplicate";
}

public static class MergeOperationStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) => status is Completed or Failed;
}

public sealed class MergeOperation
{
    public Guid Id { get; set; }
    public Guid MergeJobId { get; set; }
    public Guid AccountId { get; set; }
    public int Sequence { get; set; }
    public string OperationType { get; set; } = null!;
    public Guid CounterpartyId { get; set; }
    public string Status { get; set; } = MergeOperationStatuses.Pending;
    public string? SalesReturnRequestJson { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public MergeJob MergeJob { get; set; } = null!;
}
