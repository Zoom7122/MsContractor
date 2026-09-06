namespace MsContractor.CatalogSyncService.Models;

public sealed class DocumentAdditionalData
{
    public Guid DocumentId { get; set; }
    public string RawJson { get; set; } = null!;
}
