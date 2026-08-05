namespace MsContractor.CatalogSyncService.Repo;

public sealed class Counterparty
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Description { get; set; }
    public bool Archived { get; set; }
    public string NormalizedName { get; set; } = string.Empty;
    public string? NormalizedPhone { get; set; }
    public string? NormalizedEmail { get; set; }
    public string? NormalizedInn { get; set; }
    public string? NormalizedKpp { get; set; }
    public DateTimeOffset? MoySkladUpdatedAt { get; set; }
    public Guid LastSyncRunId { get; set; }
    public SyncRun LastSyncRun { get; set; } = null!;
    public string RawJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
