namespace MsContractor.Contracts.Internal;

public sealed record SyncRunState(
    Guid Id,
    string Status,
    int ProcessedCount,
    int TotalCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    string ExecutionMode,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record MergeOperationState(
    Guid Id,
    string Type,
    Guid CounterpartyId,
    string Status,
    int AttemptCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record MergeJobState(
    Guid Id,
    Guid MessageId,
    Guid CorrelationId,
    Guid AccountId,
    Guid MainCounterpartyId,
    Guid RequestedByUserId,
    string Status,
    int PayloadVersion,
    string Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MergeOperationState> Operations);

public sealed record MoySkladConnectionState(
    bool Connected,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record CatalogStateResponse(
    Guid AccountId,
    DateTimeOffset CheckedAt,
    bool DatabaseConnected,
    int CounterpartyCount,
    SyncRunState? LatestSyncRun,
    IReadOnlyList<MergeJobState> LatestMergeJobs,
    MoySkladConnectionState MoySklad);
