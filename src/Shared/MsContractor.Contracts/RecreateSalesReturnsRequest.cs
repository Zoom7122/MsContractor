using System.Text.Json.Nodes;

namespace MsContractor.Contracts.Internal;

public sealed record RecreateSalesReturnsRequest(Guid MainCounterpartyId, IReadOnlyList<RecreateSalesReturnItem> Documents);
public sealed record RecreateSalesReturnItem(Guid OldDocumentId, Guid DuplicateCounterpartyId,
    Guid? NewAgentAccountId, Guid? NewContractId, SalesReturnCopyData Data);

public sealed class SalesReturnCopyData
{
    public string? Name { get; init; }
    public string? Moment { get; init; }
    public bool? Applicable { get; init; }
    public string? Description { get; init; }
    public string? Code { get; init; }
    public string? ExternalCode { get; init; }
    public JsonObject? Organization { get; init; }
    public JsonObject? OrganizationAccount { get; init; }
    public JsonObject? Agent { get; init; }
    public JsonObject? AgentAccount { get; init; }
    public JsonObject? Store { get; init; }
    public JsonObject? Demand { get; init; }
    public JsonObject? Contract { get; init; }
    public JsonObject? Project { get; init; }
    public JsonObject? State { get; init; }
    public JsonObject? SalesChannel { get; init; }
    public JsonObject? Rate { get; init; }
    public bool? VatEnabled { get; init; }
    public bool? VatIncluded { get; init; }
    public JsonObject[]? Attributes { get; init; }
    public JsonObject[]? Positions { get; init; }
    public JsonObject? Owner { get; init; }
    public JsonObject? Group { get; init; }
    public bool? Shared { get; init; }
}

public sealed record RecreateSalesReturnsResponse(Guid OperationId, Guid MainCounterpartyId,
    IReadOnlyList<RecreateSalesReturnResult> Documents);
public sealed record RecreateSalesReturnResult(Guid OldDocumentId, Guid? NewDocumentId,
    string Stage, string Status, string? ErrorCode, string? Error, bool Retryable, SalesReturnCopyData? Data = null);
