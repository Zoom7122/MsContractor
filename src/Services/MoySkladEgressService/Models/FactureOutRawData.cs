namespace MsContractor.MoySkladEgressService.Models;

public sealed class FactureOutRawData
{
    public Guid AccountId { get; set; }

    public Guid DocumentId { get; set; }

    public string RawJson { get; set; } = string.Empty;
}
