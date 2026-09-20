namespace MergeVerifier.Models;

public sealed record MergeSnapshot(
    string SnapshotVersion,
    Guid AccountId,
    Guid MainCounterpartyId,
    IReadOnlyList<Guid> DuplicateCounterpartyIds,
    DateTimeOffset CapturedAt,
    IReadOnlyList<CounterpartySnapshot> Counterparties,
    IReadOnlyList<DocumentSnapshot> Documents,
    IReadOnlyList<string> DocumentTypes);

public sealed record CounterpartySnapshot(
    Guid CounterpartyId,
    string Role,
    bool? Archived,
    string RawJson,
    string NormalizedJson);

public sealed record DocumentSnapshot(
    string DocumentType,
    Guid DocumentId,
    Guid SourceCounterpartyId,
    string? Name,
    string? ExternalCode,
    string? Moment,
    decimal? Sum,
    Guid? AgentId,
    IReadOnlyList<string> PositionRawJson,
    string RawJson,
    string NormalizedJson,
    string NormalizedHash);
