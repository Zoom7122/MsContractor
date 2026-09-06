namespace MsContractor.CatalogSyncService.Models;

public static class MergeJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string PartiallyCompleted = "partially_completed";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) =>
        status is Completed or PartiallyCompleted or Failed;
}

public sealed class MergeJob
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid MainCounterpartyId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public string Status { get; set; } = MergeJobStatuses.Pending;
    public int PayloadVersion { get; set; } = 1;
    public string Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<MergeOperation> Operations { get; set; } = [];
}
