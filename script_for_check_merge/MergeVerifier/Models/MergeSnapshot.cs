using System.Text.Json;
using MergeVerifier.Documents;

namespace MergeVerifier.Models;

public sealed record DocumentSnapshot
{
    public required string EntityType { get; init; }
    public required DocumentTransferMode TransferMode { get; init; }
    public required Guid SourceCounterpartyId { get; init; }
    public string? StableDocumentId { get; init; }
    public required JsonElement Data { get; init; }
}

public sealed record CaptureCoverage(string EntityType, bool Fetched, string? Note = null);

public sealed record MergeSnapshot
{
    public int SnapshotVersion { get; init; } = 1;
    public required Guid MainCounterpartyId { get; init; }
    public required IReadOnlyList<Guid> DuplicateCounterpartyIds { get; init; }
    public required DateTimeOffset CaptureStartedAt { get; init; }
    public required DateTimeOffset CaptureCompletedAt { get; init; }
    public required IReadOnlyList<CaptureCoverage> Coverage { get; init; }
    public required IReadOnlyList<DocumentSnapshot> Documents { get; init; }
}
