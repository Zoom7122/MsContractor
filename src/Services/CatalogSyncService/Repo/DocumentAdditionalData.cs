namespace MsContractor.CatalogSyncService.Repo;

public sealed class DocumentAdditionalData
{
    public Guid DocumentId { get; set; }
    public string RawJson { get; set; } = null!;
}
