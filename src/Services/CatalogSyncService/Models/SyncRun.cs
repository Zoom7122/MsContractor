namespace MsContractor.CatalogSyncService.Models;

public sealed class SyncRun
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid AccountId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string RequestedMode { get; set; } = "full";
    public string ExecutionMode { get; set; } = "full";
    public string Status { get; set; } = "queued";
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public DateTimeOffset? WindowFrom { get; set; }
    public DateTimeOffset? WindowTo { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<Counterparty> Counterparties { get; set; } = new List<Counterparty>();
}
