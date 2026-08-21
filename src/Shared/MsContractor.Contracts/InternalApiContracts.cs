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
    Guid CounterpartyId);

public sealed record MoySkladDocumentTypeCount(
    string DocumentType,
    int Count);

public sealed record MoySkladDocumentDiscoveryResponse(
    IReadOnlyList<MoySkladDocumentReference> Documents,
    IReadOnlyList<MoySkladDocumentTypeCount> Counts);
