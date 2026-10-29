namespace MsContractor.CatalogSyncService.Models;

public sealed class DocumentAdditionalCommission
{
    public Guid AccountId { get; set; }
    public string DocumentType { get; set; } = null!;
    public Guid DocumentId { get; set; }
    public Guid? Contract { get; set; }
}
