using System.Text.Json;
using MergeVerifier.Documents;

namespace MergeVerifier.Models;

public enum VerificationStatus { Matched, Missing, Changed, Unexpected, StillOnDuplicate, Unsupported, FetchError }

public sealed record FieldDifference(string Path, JsonElement? Before, JsonElement? After);

public sealed record DocumentComparisonResult
{
    public required VerificationStatus Status { get; init; }
    public int Count { get; init; } = 1;
    public string? StableDocumentId { get; init; }
    public string? SemanticHash { get; init; }
    public Guid? SourceCounterpartyId { get; init; }
    public JsonElement? Data { get; init; }
    public IReadOnlyList<FieldDifference> Differences { get; init; } = [];
    public string? Note { get; init; }
}

public sealed record TypeComparisonResult(string EntityType, DocumentTransferMode TransferMode,
    int Before, int AfterMain, IReadOnlyList<DocumentComparisonResult> Documents)
{
    public int Count(VerificationStatus status) => Documents.Where(x => x.Status == status).Sum(x => x.Count);
    public bool Passed => Documents.All(x => x.Status == VerificationStatus.Matched);
}

public sealed record VerificationReport
{
    public int ReportVersion { get; init; } = 1;
    public required DateTimeOffset GeneratedAt { get; init; }
    public required Guid MainCounterpartyId { get; init; }
    public required IReadOnlyList<TypeComparisonResult> Types { get; init; }
    public string? Error { get; init; }
    public bool AfterCaptureCompleted { get; init; } = true;
    public bool Passed => Error is null && AfterCaptureCompleted && Types.All(x => x.Passed);
    public int ExitCode => Error is not null || !AfterCaptureCompleted ? 2 : Passed ? 0 : 1;
    public int TotalBefore => Types.Sum(x => x.Before);
    public IReadOnlyDictionary<VerificationStatus, int> Totals => Enum.GetValues<VerificationStatus>()
        .ToDictionary(status => status, status => Types.Sum(x => x.Count(status)));
}
