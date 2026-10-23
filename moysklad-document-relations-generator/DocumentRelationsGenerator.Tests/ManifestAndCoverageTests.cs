using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Cleanup;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Reporting;

namespace DocumentRelationsGenerator.Tests;

public sealed class ManifestAndCoverageTests
{
    private static RunManifest SampleManifest() => new()
    {
        RunId = "20260924-221530-a81f",
        Seed = 7348291,
        AnchorDate = "2026-09-24",
        StartedAt = new DateTimeOffset(2026, 9, 24, 22, 15, 30, TimeSpan.FromHours(3)),
        BaseUrl = "https://api.moysklad.ru/api/remap/1.2/",
        Counterparties = [new ManifestEntity { Role = "counterparty", Type = "counterparty", Id = "cp", Href = "h/cp", Created = true, Sequence = 3 }],
        Entities =
        [
            new ManifestEntity { Role = "organization", Type = "organization", Id = "org", Href = "h/org", Created = false },
            new ManifestEntity { Role = "product", Type = "product", Id = "p1", Href = "h/p1", Created = true, Sequence = 1 },
            new ManifestEntity { Role = "store", Type = "store", Id = "st", Href = "h/st", Created = false },
            new ManifestEntity { Role = "project", Type = "project", Id = "pr", Href = "h/pr", Created = true, Sequence = 2 },
            new ManifestEntity { Role = "contractSales", Type = "contract", Id = "c1", Href = "h/c1", Created = true, Sequence = 4 }
        ],
        Scenarios =
        [
            new ManifestScenario
            {
                Name = "sales-return-flow", Status = "passed",
                Documents =
                [
                    new ManifestDocument { Key = "demand", Type = "demand", Id = "d", Href = "h/d", Sequence = 5 },
                    new ManifestDocument { Key = "salesreturn", Type = "salesreturn", Id = "r", Href = "h/r", Sequence = 6, DependsOn = ["demand"] },
                    new ManifestDocument { Key = "loss", Type = "loss", Id = "l", Href = "h/l", Sequence = 7, DependsOn = ["salesreturn"] }
                ],
                Relations =
                [
                    new ManifestRelation
                    {
                        RelationId = "salesreturn.demand->demand", SourceType = "salesreturn", SourceId = "r", Field = "demand",
                        TargetType = "demand", TargetId = "d", Verified = true, Status = "verified"
                    }
                ],
                Errors = [new ManifestError { Operation = "POST create", RequestPayload = new JsonObject { ["name"] = "x" } }]
            }
        ]
    };

    [Fact]
    public void Manifest_RoundTripsWithCamelCaseAndNoSecrets()
    {
        var json = ManifestStore.Serialize(SampleManifest());
        var restored = ManifestStore.Deserialize(json);

        Assert.Equal(json, ManifestStore.Serialize(restored));
        Assert.Contains("\"seed\": 7348291", json);
        Assert.Contains("\"sourceType\": \"salesreturn\"", json);
        Assert.Contains("\"verified\": true", json);
        foreach (var forbidden in new[] { "password", "token", "authorization", "login" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestStore_WritesAtomicallyAndContinuesSequence()
    {
        var directory = Directory.CreateTempSubdirectory("reltest-manifest-");
        try
        {
            var path = Path.Combine(directory.FullName, "run", "manifest.json");
            var store = new ManifestStore(path, SampleManifest());
            Assert.Equal(8, store.NextSequence());
            store.Save();
            store.Save();

            Assert.Equal("20260924-221530-a81f", ManifestStore.Load(path).RunId);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temp files left behind
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsForeignJson()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"tool\":\"something-else\",\"runId\":\"x\"}");
            Assert.Throws<GeneratorException>(() => ManifestStore.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Coverage_ClassifiesVerifiedFailedSkippedAndNotRun()
    {
        var catalog = new[]
        {
            RelationCatalog.Get("salesreturn.demand->demand"),
            RelationCatalog.Get("loss.salesReturn->salesreturn"),
            RelationCatalog.Get("retaildemand.retailShift->retailshift"),
            RelationCatalog.Get("supply.purchaseOrder->purchaseorder")
        };
        var scenarios = new List<ManifestScenario>
        {
            new()
            {
                Status = "failed",
                Relations =
                [
                    new ManifestRelation { RelationId = "salesreturn.demand->demand", SourceId = "r", Verified = true },
                    new ManifestRelation { RelationId = "loss.salesReturn->salesreturn", SourceId = "l", Verified = false }
                ]
            },
            new() { Status = "skipped", Relations = [new ManifestRelation { RelationId = "retaildemand.retailShift->retailshift" }] }
        };

        var summary = CoverageCalculator.Calculate(catalog, scenarios);

        Assert.Equal([RelationCoverageStatus.Verified, RelationCoverageStatus.Failed, RelationCoverageStatus.Skipped, RelationCoverageStatus.NotRun],
            summary.Rows.Select(row => row.Status));
        Assert.Equal((4, 2, 2, 1, 1, 1, 25.0),
            (summary.Confirmed, summary.Planned, summary.Created, summary.Verified, summary.Failed, summary.Skipped, summary.Percent));
        Assert.Equal(1, CoverageCalculator.ExitCode(summary, scenarios));
    }

    [Fact]
    public void Coverage_ExitCodeIsZeroOnlyWhenEverythingExpectedWasVerified()
    {
        var relation = RelationCatalog.Get("salesreturn.demand->demand");
        var passed = new List<ManifestScenario>
        {
            new() { Status = "passed", Relations = [new ManifestRelation { RelationId = relation.Id, SourceId = "r", Verified = true }] }
        };
        var summary = CoverageCalculator.Calculate([relation], passed);
        Assert.Equal(100, summary.Percent);
        Assert.Equal(0, CoverageCalculator.ExitCode(summary, passed));

        passed.Add(new ManifestScenario { Status = "skipped" });
        Assert.Equal(3, CoverageCalculator.ExitCode(CoverageCalculator.Calculate([relation], passed), passed));
    }

    [Fact]
    public void CoverageReport_PrintsOneLinePerConfirmedRelation()
    {
        var output = new StringWriter();
        CoverageCalculator.Print(CoverageCalculator.Calculate(RelationCatalog.All, []), output);
        var text = output.ToString();
        Assert.Contains("DOCUMENT RELATION COVERAGE", text);
        Assert.Contains($"Confirmed relations:  {RelationCatalog.All.Count}", text);
        Assert.All(RelationCatalog.All, relation => Assert.Contains(relation.Label, text));
    }

    [Fact]
    public void CleanupPlan_DeletesChildrenFirstThenOnlyCreatedEntitiesInReverseOrder()
    {
        var plan = CleanupPlanner.Plan(SampleManifest());

        Assert.Equal(["l", "r", "d", "c1", "cp", "pr", "p1"], plan.Select(item => item.Id));
        Assert.DoesNotContain(plan, item => item.Id is "org" or "st"); // reused account entities are never deleted
    }
}
