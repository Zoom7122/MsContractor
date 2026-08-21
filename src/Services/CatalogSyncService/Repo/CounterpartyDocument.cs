namespace MsContractor.CatalogSyncService.Repo;

public sealed class CounterpartyDocument
{
    public Guid AccountId { get; set; }
    public Guid CounterpartyId { get; set; }
    public string DocumentType { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
