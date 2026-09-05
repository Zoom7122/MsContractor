namespace MsContractor.CatalogSyncService.Repo;

public sealed class DocumentAdditionalCommission
{
    public Guid DocumentId { get; set; }
    public Guid? Contract { get; set; }
}
