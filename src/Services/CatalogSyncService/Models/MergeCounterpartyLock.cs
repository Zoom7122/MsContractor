namespace MsContractor.CatalogSyncService.Models;

public sealed class MergeCounterpartyLock
{
    public Guid AccountId { get; set; }
    public Guid CounterpartyId { get; set; }
    public Guid MergeJobId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public MergeJob MergeJob { get; set; } = null!;
}
