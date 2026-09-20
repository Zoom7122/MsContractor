namespace MsContractor.MoySkladEgressService.Models;

public sealed class PurchaseReturnPositionRawData
{
    public Guid AccountId { get; set; }

    public Guid PurchaseReturnId { get; set; }

    public Guid PositionId { get; set; }

    public string RawJson { get; set; } = string.Empty;
}
