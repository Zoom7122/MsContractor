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
    /// <summary>2: agentAccount ownership, recreated syncId, linkKey in references to recreated documents.</summary>
    public const int CurrentVersion = 2;

    public int SnapshotVersion { get; init; } = CurrentVersion;
    public required Guid MainCounterpartyId { get; init; }
    public required IReadOnlyList<Guid> DuplicateCounterpartyIds { get; init; }
    public required DateTimeOffset CaptureStartedAt { get; init; }
    public required DateTimeOffset CaptureCompletedAt { get; init; }
    public required IReadOnlyList<CaptureCoverage> Coverage { get; init; }
    public required IReadOnlyList<DocumentSnapshot> Documents { get; init; }
}
