namespace MsContractor.CatalogSyncService.Models;

public sealed class CatalogSettings
{
    public Guid AccountId { get; set; }
    public bool IncludeArchivedWithDocuments { get; set; }
    public int GroupLimit { get; set; }
    public int ItemLimit { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
