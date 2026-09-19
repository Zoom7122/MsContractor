namespace MsContractor.MoySkladEgressService.Models;

public sealed class SalesReturnPositionRawData
{
    public Guid AccountId { get; set; }

    public Guid SalesReturnId { get; set; }

    public Guid PositionId { get; set; }

    public string RawJson { get; set; } = string.Empty;
}
