namespace MsContractor.MoySkladEgressService.Models;

public sealed class FactureInRecreationItem
{
    public Guid AccountId { get; set; }

    public Guid SourceFactureInId { get; set; }

    public Guid MainCounterpartyId { get; set; }

    public Guid? SourceSyncId { get; set; }

    public Guid NewSyncId { get; set; }

    public Guid? NewFactureInId { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string Stage { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
