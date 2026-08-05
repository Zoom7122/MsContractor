using System.Text.Json.Serialization;

namespace MsContractor.Contracts.Sync;

[JsonConverter(typeof(JsonStringEnumConverter<SyncMode>))]
public enum SyncMode
{
    Full = 0,
    Incremental = 1
}

public static class SyncTopics
{
    public const string Commands = "mscontractor.commands.sync";
    public const string Events = "mscontractor.events.sync";
}

public sealed record SyncRequested(
    Guid MessageId,
    Guid SyncRunId,
    Guid AccountId,
    Guid RequestedByUserId,
    DateTimeOffset RequestedAt,
    SyncMode Mode = SyncMode.Full);

public sealed record SyncStartRequest(
    Guid AccountId,
    Guid RequestedByUserId,
    SyncMode Mode = SyncMode.Full);

public sealed record SyncCompleted(
    Guid EventId,
    Guid SyncRunId,
    Guid AccountId,
    int ProcessedCount,
    DateTimeOffset CompletedAt);

public sealed record SyncFailed(
    Guid EventId,
    Guid SyncRunId,
    Guid AccountId,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset FailedAt);

public sealed record SyncAccepted(Guid SyncRunId, string Status);
