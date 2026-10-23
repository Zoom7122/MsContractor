using System.Text.Json.Nodes;

namespace DocumentRelationsGenerator.Manifest;

/// <summary>
/// generated-data/&lt;runId&gt;/manifest.json. Written after every created object, so an interrupted run
/// can still be cleaned up. Holds IDs and business data only, never credentials or headers.
/// </summary>
public sealed class RunManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Tool { get; set; } = "moysklad-document-relations-generator";
    public string RunId { get; set; } = "";
    public int Seed { get; set; }
    public string AnchorDate { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>running, completed, failed, interrupted.</summary>
    public string Status { get; set; } = "running";

    public string BaseUrl { get; set; } = "";
    public int CounterpartyCount { get; set; }
    public List<string> SelectedScenarios { get; set; } = [];
    public List<ManifestEntity> Counterparties { get; set; } = [];

    /// <summary>Reference entities: reused ones (<c>created=false</c>, never deleted) and test ones.</summary>
    public List<ManifestEntity> Entities { get; set; } = [];

    public List<ManifestScenario> Scenarios { get; set; } = [];
    public ManifestCoverage? Coverage { get; set; }
}

public sealed class ManifestEntity
{
    public string Role { get; set; } = "";
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string Href { get; set; } = "";
    public bool Created { get; set; }
    public string? ExternalCode { get; set; }
    public int? CounterpartyIndex { get; set; }

    /// <summary>Global creation order; cleanup deletes in descending order.</summary>
    public int Sequence { get; set; }
}

public sealed class ManifestScenario
{
    public string Name { get; set; } = "";
    public int CounterpartyIndex { get; set; }
    public string? CounterpartyId { get; set; }

    /// <summary>running, passed, failed, skipped.</summary>
    public string Status { get; set; } = "running";

    public string? SkipReason { get; set; }
    public bool WithContract { get; set; }
    public bool WithAgentAccount { get; set; }
    public bool WithForeignCurrency { get; set; }
    public List<ManifestDocument> Documents { get; set; } = [];
    public List<ManifestRelation> Relations { get; set; } = [];
    public List<ManifestError> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ManifestDocument
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string Href { get; set; } = "";
    public string? Moment { get; set; }
    public string? ExternalCode { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public int Sequence { get; set; }
}

public sealed class ManifestRelation
{
    public string RelationId { get; set; } = "";
    public string SourceStep { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string? SourceId { get; set; }
    public string Field { get; set; } = "";
    public string TargetStep { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }

    /// <summary>verified, failed, not-created.</summary>
    public string Status { get; set; } = "not-created";

    public bool Verified { get; set; }
    public string? ReverseField { get; set; }
    public bool? ReverseVerified { get; set; }
    public string? Detail { get; set; }
}

public sealed class ManifestError
{
    public string Scenario { get; set; } = "";
    public string Step { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string Operation { get; set; } = "";
    public int? HttpStatus { get; set; }
    public int? ErrorCode { get; set; }
    public string? Message { get; set; }
    public bool OutcomeUnknown { get; set; }

    /// <summary>Business payload of the failed create request (no headers or credentials).</summary>
    public JsonObject? RequestPayload { get; set; }
}

public sealed class ManifestCoverage
{
    public int Confirmed { get; set; }
    public int Planned { get; set; }
    public int Created { get; set; }
    public int Verified { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public double CoveragePercent { get; set; }
    public List<ManifestCoverageRow> Rows { get; set; } = [];
}

public sealed class ManifestCoverageRow
{
    public string RelationId { get; set; } = "";
    public string Status { get; set; } = "";
    public int VerifiedInstances { get; set; }
}
