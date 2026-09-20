namespace MsContractor.MoySkladEgressService.Models;

public sealed record PurchaseReturnRecreationReferences(
    Guid? ContractId,
    Guid? AgentAccountId);

public sealed record PurchaseReturnPreparedDocument(
    Guid SourceDocumentId,
    Guid SyncId,
    Guid? ContractId,
    Guid? AgentAccountId,
    string PayloadJson);

public sealed record PurchaseReturnVerificationInput(
    Guid SourceDocumentId,
    Guid NewDocumentId,
    Guid SyncId,
    Guid? ContractId,
    Guid? AgentAccountId,
    string? CreatedRawJson = null);
