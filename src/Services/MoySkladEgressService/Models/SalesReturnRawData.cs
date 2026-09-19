namespace MsContractor.MoySkladEgressService.Models;

public sealed class SalesReturnRawData
{
    public Guid AccountId { get; set; }

    public Guid DocumentId { get; set; }

    public string RawJson { get; set; } = string.Empty;
}
