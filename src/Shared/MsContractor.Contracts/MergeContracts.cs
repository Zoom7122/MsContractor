using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsContractor.Contracts.Merge;

public static class MergeTopics
{
    public const string Commands = "mscontractor.commands.merge";
    public const string DeadLetters = "mscontractor.commands.merge.dlq";
}

public sealed record MergeMainCounterpartyDto(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    IReadOnlyList<MergeMainCounterpartyAttributeDto>? Attributes = null);

public sealed record MergeMainCounterpartyAttributeDto(
    Guid Id,
    string Type,
    Guid SourceCounterpartyId,
    JsonElement? Value,
    JsonElement? File = null,
    bool Clear = false,
    string? ValueJson = null,
    string? FileJson = null);

public sealed record CounterpartyAttributeDto(
    Guid Id,
    string? Name,
    string? Type,
    JsonElement? Value,
    JsonElement? File,
    string ValueJson,
    string FileJson);

public sealed record CreateMergeJobRequest(
    Guid MainCounterpartyId,
    IReadOnlyList<Guid> DuplicateCounterpartyIds,
    MergeMainCounterpartyDto MainCounterparty);

public sealed record MergeSelectionPreviewRequest(
    IReadOnlyList<Guid> CounterpartyIds);

public sealed record MergeSelectionCounterpartyDto(
    Guid Id,
    string Name,
    string? Description,
    string? Email,
    string? Phone,
    bool Archived,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<CounterpartyAttributeDto> Attributes);

public sealed record MergeSelectionPreviewResponse(
    IReadOnlyList<MergeSelectionCounterpartyDto> Counterparties);

public sealed record MergeJobAccepted(Guid MergeJobId, string Status);

public sealed record MergeRequested(
    int SchemaVersion,
    Guid MessageId,
    Guid CorrelationId,
    Guid MergeJobId,
    Guid AccountId,
    Guid MainCounterpartyId,
    IReadOnlyList<Guid> DuplicateCounterpartyIds,
    MergeMainCounterpartyDto MainCounterparty,
    Guid RequestedByUserId,
    DateTimeOffset RequestedAt);

public sealed record MergeDeadLetter(
    Guid DeadLetterId,
    Guid? MessageId,
    string SourceTopic,
    int Partition,
    long Offset,
    string ErrorCode,
    DateTimeOffset FailedAt,
    string PayloadSha256);
