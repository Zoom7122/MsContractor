namespace MsContractor.CatalogSyncService.Repo;

public sealed class SyncWatermark
{
    public Guid AccountId { get; set; }
    public DateTimeOffset Watermark { get; set; }
    public Guid LastSyncRunId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
