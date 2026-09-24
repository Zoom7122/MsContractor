using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.Relations;

namespace DocumentRelationsGenerator.Reporting;

public enum RelationCoverageStatus
{
    /// <summary>Every instance created in this run was confirmed by GET.</summary>
    Verified,

    /// <summary>The run was expected to create the relation, and at least one instance is missing or unconfirmed.</summary>
    Failed,

    /// <summary>Only in scenarios skipped for a missing prerequisite (for example, retail store).</summary>
    Skipped,

    /// <summary>No selected scenario contains the relation.</summary>
    NotRun,

    /// <summary>Dry-run: a selected scenario would create it.</summary>
    Planned
}

public sealed record RelationCoverageRow(RelationDefinition Relation, RelationCoverageStatus Status, int Instances,
    int CreatedInstances, int VerifiedInstances);

public sealed record CoverageSummary(IReadOnlyList<RelationCoverageRow> Rows)
{
    public int Confirmed => Rows.Count;
    public int Planned => Rows.Count(row => row.Instances > 0);
    public int Created => Rows.Count(row => row.CreatedInstances > 0);
    public int Verified => Rows.Count(row => row.Status == RelationCoverageStatus.Verified);
    public int Failed => Rows.Count(row => row.Status == RelationCoverageStatus.Failed);
    public int Skipped => Rows.Count(row => row.Status == RelationCoverageStatus.Skipped);
    public double Percent => Confirmed == 0 ? 0 : Math.Floor(1000.0 * Verified / Confirmed) / 10;

    public ManifestCoverage ToManifest() => new()
    {
        Confirmed = Confirmed,
        Planned = Planned,
        Created = Created,
        Verified = Verified,
        Failed = Failed,
        Skipped = Skipped,
        CoveragePercent = Percent,
        Rows = Rows.Select(row => new ManifestCoverageRow
        {
            RelationId = row.Relation.Id,
            Status = row.Status.ToString().ToLowerInvariant(),
            VerifiedInstances = row.VerifiedInstances
        }).ToList()
    };
}

public static class CoverageCalculator
{
    public static CoverageSummary Calculate(IReadOnlyList<RelationDefinition> catalog, IReadOnlyList<ManifestScenario> scenarios)
    {
        var executed = scenarios.Where(scenario => scenario.Status != "skipped").SelectMany(scenario => scenario.Relations).ToList();
        var skipped = scenarios.Where(scenario => scenario.Status == "skipped").SelectMany(scenario => scenario.Relations).ToList();
        return new CoverageSummary(catalog.Select(relation =>
        {
            var instances = executed.Where(item => item.RelationId == relation.Id).ToList();
            var verified = instances.Count(item => item.Verified);
            var status = instances.Count > 0
                ? verified == instances.Count ? RelationCoverageStatus.Verified : RelationCoverageStatus.Failed
                : skipped.Any(item => item.RelationId == relation.Id) ? RelationCoverageStatus.Skipped : RelationCoverageStatus.NotRun;
            return new RelationCoverageRow(relation, status, instances.Count, instances.Count(item => item.SourceId is not null), verified);
        }).ToList());
    }

    /// <summary>Coverage a dry-run expects: relations of runnable selected scenarios.</summary>
    public static CoverageSummary Expected(IReadOnlyList<RelationDefinition> catalog,
        IReadOnlyCollection<string> plannedRelationIds, IReadOnlyCollection<string> skippedRelationIds) =>
        new(catalog.Select(relation =>
        {
            var planned = plannedRelationIds.Count(id => id == relation.Id);
            var status = planned > 0 ? RelationCoverageStatus.Planned
                : skippedRelationIds.Contains(relation.Id) ? RelationCoverageStatus.Skipped
                : RelationCoverageStatus.NotRun;
            return new RelationCoverageRow(relation, status, planned, 0, 0);
        }).ToList());

    /// <summary>0 success; 1 an expected relation or document failed; 3 nothing failed but prerequisites were missing.</summary>
    public static int ExitCode(CoverageSummary summary, IReadOnlyList<ManifestScenario> scenarios)
    {
        if (summary.Failed > 0 || scenarios.Any(scenario => scenario.Status == "failed")) return 1;
        return scenarios.Any(scenario => scenario.Status == "skipped") ? 3 : 0;
    }

    public static void Print(CoverageSummary summary, TextWriter output, bool dryRun = false)
    {
        const string line = "====================================";
        output.WriteLine();
        output.WriteLine(line);
        output.WriteLine(dryRun ? "EXPECTED DOCUMENT RELATION COVERAGE (dry-run)" : "DOCUMENT RELATION COVERAGE");
        output.WriteLine(line);
        output.WriteLine();
        foreach (var row in summary.Rows)
        {
            var status = row.Status switch
            {
                RelationCoverageStatus.Verified => "OK",
                RelationCoverageStatus.Failed => $"FAIL ({row.VerifiedInstances}/{row.Instances} verified)",
                RelationCoverageStatus.Skipped => "SKIPPED",
                RelationCoverageStatus.Planned => "PLANNED",
                _ => "NOT RUN"
            };
            output.WriteLine($"{row.Relation.Label,-62} {status}");
        }

        output.WriteLine("------------------------------------");
        output.WriteLine($"Confirmed relations:  {summary.Confirmed}");
        if (dryRun)
        {
            output.WriteLine($"Planned relations:    {summary.Planned}");
            output.WriteLine($"Skipped relations:    {summary.Skipped}");
            output.WriteLine($"Expected coverage:    {Math.Floor(1000.0 * summary.Planned / Math.Max(1, summary.Confirmed)) / 10}%");
        }
        else
        {
            output.WriteLine($"Created relations:    {summary.Created}");
            output.WriteLine($"Verified relations:   {summary.Verified}");
            output.WriteLine($"Failed relations:     {summary.Failed}");
            if (summary.Skipped > 0) output.WriteLine($"Skipped relations:    {summary.Skipped}");
            output.WriteLine($"Coverage:             {summary.Percent}%");
        }

        output.WriteLine(line);
    }
}
