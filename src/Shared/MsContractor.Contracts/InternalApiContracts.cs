using System.Text.Json.Serialization;

namespace MsContractor.Contracts.Internal;

public static class InternalApiHeaders
{
    public const string ApiKey = "X-Internal-Api-Key";
    public const string AccountId = "X-Account-Id";
    public const string CorrelationId = "X-Correlation-Id";
    public const string SyncRunId = "X-Sync-Run-Id";
    public const string UserId = "X-User-Id";
    public const string MergeJobId = "X-Merge-Job-Id";
    public const string OperationId = "X-Merge-Operation-Id";
}

public sealed record InternalAccessTokenResponse(string AccessToken);

public sealed record InternalErrorResponse(string Code, string Message);

public sealed record InternalCounterpartyUpdateRequest(
    string Name,
    string? Email,
    string? Phone,
    string? Description);

public sealed record InternalCounterpartyBatchArchiveRequest(
    IReadOnlyList<Guid> CounterpartyIds);

public sealed record MoySkladDocumentDiscoveryRequest(
    IReadOnlyList<Guid>? CounterpartyIds);

public sealed record MoySkladDocumentReference(
    string DocumentType,
    Guid DocumentId,
    Guid CounterpartyId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ContractId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RawJson = null);

public sealed record MoySkladDocumentTypeCount(
    string DocumentType,
    int Count);

public sealed record MoySkladDocumentDiscoveryResponse(
    IReadOnlyList<MoySkladDocumentReference> Documents,
    IReadOnlyList<MoySkladDocumentTypeCount> Counts);

public sealed record MoySkladDocumentChangeCounterpartyRequest(
    Guid MainCounterpartyId,
    IReadOnlyList<MoySkladDocumentChangeItem>? Documents);

public sealed record MoySkladDocumentChangeItem(
    string DocumentType,
    Guid DocumentId);

public sealed record MoySkladDocumentChangeAgentAndContractRequest(
    Guid MainCounterpartyId,
    IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem>? Documents);

public sealed record MoySkladDocumentChangeAgentAndContractItem(
    string DocumentType,
    Guid DocumentId,
    Guid? Contract);

public sealed record MoySkladDocumentChangeSkippedItem(
    string DocumentType,
    Guid DocumentId,
    string Reason);

public sealed record MoySkladDocumentChangeFailure(
    string DocumentType,
    Guid DocumentId,
    string Code,
    string Message,
    int StatusCode,
    bool Retryable,
    string? Endpoint = null,
    string? MoySkladErrorCode = null,
    string? MoySkladErrorMessage = null,
    string? ValidationError = null);

public sealed record MoySkladDocumentChangeCounterpartyResponse(
    Guid MainCounterpartyId,
    int RequestedCount,
    int ChangedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<MoySkladDocumentChangeItem> ChangedDocuments,
    IReadOnlyList<MoySkladDocumentChangeSkippedItem> SkippedDocuments,
    IReadOnlyList<MoySkladDocumentChangeFailure> Failures);
