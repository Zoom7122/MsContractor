namespace MsContractor.CatalogSyncService.Models;

public sealed record SyncRunState(
    string Status,
    int TotalCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ExecutionMode,
    string? ErrorCode,
    string? ErrorMessage);

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
    DateTimeOffset UpdatedAt);

public sealed record MoySkladConnectionState(
    bool Connected,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record CatalogStateResponse(
    Guid AccountId,
    DateTimeOffset CheckedAt,
    bool DatabaseConnected,
    SyncRunState? LatestSyncRun,
    IReadOnlyList<MergeJobState> LatestMergeJobs,
    MoySkladConnectionState MoySklad);
