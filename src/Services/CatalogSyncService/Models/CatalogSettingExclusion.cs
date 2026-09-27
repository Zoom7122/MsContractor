namespace MsContractor.CatalogSyncService.Models;

public sealed class CatalogSettingExclusion
{
    public Guid AccountId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
