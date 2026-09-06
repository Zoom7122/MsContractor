namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladDocumentPageRow(
    Guid DocumentId,
    string AgentHref,
    string? AgentType,
    Guid? ContractId = null,
    string? RawJson = null);
