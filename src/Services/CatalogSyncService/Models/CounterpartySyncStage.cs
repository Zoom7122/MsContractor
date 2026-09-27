namespace MsContractor.CatalogSyncService.Models;

/// <summary>
/// A durable, run-scoped copy of an incoming counterparty. Rows stay here until the
/// whole Egress snapshot has been validated and can be published atomically.
/// </summary>
public sealed class CounterpartySyncStage
{
    public Guid AccountId { get; set; }
    public Guid SyncRunId { get; set; }
    public long Sequence { get; set; }
    public Guid CounterpartyId { get; set; }
    public bool IsValidForStorage { get; set; }
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
    public long MoySkladUpdatedSortValue { get; set; }
    public Guid LastSyncRunId { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
