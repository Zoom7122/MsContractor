namespace MsContractor.CatalogSyncService.Models;

public sealed class CatalogSettings
{
    public Guid AccountId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}
