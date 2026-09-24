using DocumentRelationsGenerator.Cli;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.Relations;

namespace DocumentRelationsGenerator.Tests;

public sealed class GeneratorApplicationTests : IDisposable
{
    private const string Password = "Pa55-word-never-printed";
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("reltest-app-");
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose() => directory.Delete(recursive: true);

    private GeneratorApplication App(FakeMoySkladApi api, bool retail = false)
    {
        var environment = new Dictionary<string, string>
        {
            ["MS_LOGIN"] = "admin@test", ["MS_PASSWORD"] = Password, ["MS_RELTEST_MIN_REQUEST_INTERVAL_MS"] = "100"
        };
        if (retail) environment["MS_RELTEST_RETAIL_STORE_ID"] = MetaReference.Id(MetaReference.Href(api.RetailStore))!;
        return new GeneratorApplication(output, error, environment, (_, _) => api,
            () => new DateTimeOffset(2026, 9, 24, 22, 15, 30, TimeSpan.FromHours(3)));
    }

    private string[] Args(params string[] args) => [.. args, "--output-dir", directory.FullName, "--seed", "7348291"];

    private RunManifest LoadManifest() =>
        ManifestStore.Load(Directory.GetFiles(directory.FullName, "manifest.json", SearchOption.AllDirectories).Single());

    [Fact]
    public async Task All_WithRetailStore_CreatesAndVerifiesEveryRelation()
    {
        var api = new FakeMoySkladApi(withRetailStore: true);

        var exitCode = await App(api, retail: true).RunAsync(Args("--all"), CancellationToken.None);

        Assert.True(exitCode == 0, output + error.ToString());
        var manifest = LoadManifest();
        Assert.Equal("completed", manifest.Status);
        Assert.All(manifest.Scenarios, scenario => Assert.Equal("passed", scenario.Status));
        Assert.Equal(RelationCatalog.All.Count, manifest.Coverage!.Verified);
        Assert.Equal(100, manifest.Coverage.CoveragePercent);
        Assert.All(manifest.Scenarios.SelectMany(scenario => scenario.Relations), relation =>
        {
            Assert.True(relation.Verified);
            Assert.NotNull(relation.SourceId);
            Assert.NotNull(relation.TargetId);
            Assert.NotEqual(false, relation.ReverseVerified);
        });
        Assert.Contains("Coverage:             100%", output.ToString());
        Assert.Contains("Seed: 7348291", output.ToString());
        Assert.DoesNotContain(Password, output.ToString() + error);
        Assert.DoesNotContain(Password, File.ReadAllText(Directory.GetFiles(directory.FullName, "manifest.json", SearchOption.AllDirectories)[0]));
    }

    [Fact]
    public async Task All_WithoutRetailStore_SkipsRetailFlowAndReturnsExitCode3()
    {
        var api = new FakeMoySkladApi(withStore: false);

        var exitCode = await App(api).RunAsync(Args("--all", "--counterparties", "2"), CancellationToken.None);

        Assert.Equal(3, exitCode);
        var manifest = LoadManifest();
        Assert.Equal(2, manifest.Counterparties.Count);
        Assert.Contains(manifest.Entities, entity => entity.Role == "store" && entity.Created); // no store in the account
        Assert.Equal(2, manifest.Scenarios.Count(scenario => scenario.Status == "skipped"));
        Assert.Equal(6, manifest.Coverage!.Skipped);
        Assert.Equal(RelationCatalog.All.Count - 6, manifest.Coverage.Verified);
        Assert.Equal(0, manifest.Coverage.Failed);
    }

    [Fact]
    public async Task FailedParent_BlocksDescendantsButNotIndependentBranches()
    {
        var api = new FakeMoySkladApi { FailCreate = (type, _) => type == "salesreturn" };

        var exitCode = await App(api).RunAsync(Args("--scenario", "sales-return-flow"), CancellationToken.None);

        Assert.Equal(1, exitCode);
        var scenario = LoadManifest().Scenarios.Single();
        Assert.Equal("failed", scenario.Status);
        var failure = Assert.Single(scenario.Errors);
        Assert.Equal(("salesreturn", "POST create", 400, 3000), (failure.DocumentType, failure.Operation, failure.HttpStatus, failure.ErrorCode));
        Assert.NotNull(failure.RequestPayload);
        Assert.Equal(["demand", "invoiceout", "paymentin"], scenario.Documents.Select(document => document.Key).Order());
        Assert.DoesNotContain(api.Calls, call => call.Contains("entity/loss") || call.Contains("entity/paymentout") || call.Contains("entity/cashout"));
        Assert.Contains("[ERROR] scenario=sales-return-flow type=salesreturn operation=POST create status=400 code=3000", output.ToString());
        Assert.Contains("--seed 7348291 --anchor-date 2026-09-24", output.ToString());
    }

    [Fact]
    public async Task DryRun_ReadsOnlyAndWritesNothing()
    {
        var api = new FakeMoySkladApi();

        var exitCode = await App(api).RunAsync(Args("--dry-run"), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.All(api.Calls, call => Assert.StartsWith("GET ", call));
        Assert.Empty(Directory.GetFiles(directory.FullName, "*", SearchOption.AllDirectories));
        Assert.Contains("EXPECTED DOCUMENT RELATION COVERAGE", output.ToString());
        Assert.Contains("retail-flow [counterparty 1]  SKIPPED", output.ToString());
    }

    [Fact]
    public async Task Cleanup_DeletesExactlyTheManifestObjectsChildrenFirst()
    {
        var api = new FakeMoySkladApi();
        Assert.Equal(0, await App(api).RunAsync(Args("--scenario", "purchase-return-flow"), CancellationToken.None));
        var manifestPath = Directory.GetFiles(directory.FullName, "manifest.json", SearchOption.AllDirectories).Single();
        var manifest = ManifestStore.Load(manifestPath);
        var untouched = new[] { api.Organization, api.Store!, api.ExpenseItem, api.Employee }
            .Select(entity => MetaReference.Href(entity)!).ToList();

        var exitCode = await App(api).RunAsync(["--cleanup", manifestPath], CancellationToken.None);

        Assert.True(exitCode == 0, output.ToString());
        var expected = manifest.Scenarios.SelectMany(scenario => scenario.Documents).Count() +
                       manifest.Entities.Count(entity => entity.Created) + manifest.Counterparties.Count;
        Assert.Equal(expected, api.Deleted.Count);
        Assert.StartsWith("entity/paymentout/", api.Deleted[0]); // last created document goes first
        Assert.True(api.Deleted.FindIndex(path => path.StartsWith("entity/purchasereturn/")) <
                    api.Deleted.FindIndex(path => path.StartsWith("entity/supply/")));
        Assert.True(api.Deleted.FindIndex(path => path.StartsWith("entity/contract/")) <
                    api.Deleted.FindIndex(path => path.StartsWith("entity/counterparty/")));
        Assert.All(untouched, href => Assert.DoesNotContain(api.Deleted, path => href.EndsWith(path, StringComparison.Ordinal)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(manifestPath)!, "cleanup-report-*.json"));

        // Second cleanup: everything is already gone, nothing else is touched.
        Assert.Equal(0, await App(api).RunAsync(["--cleanup", manifestPath], CancellationToken.None));
        Assert.Equal(expected, api.Deleted.Count);
    }

    [Fact]
    public async Task MissingCredentials_StopBeforeAnyRequest()
    {
        var api = new FakeMoySkladApi();
        var app = new GeneratorApplication(output, error, new Dictionary<string, string>(), (_, _) => api, () => DateTimeOffset.Now);

        Assert.Equal(2, await app.RunAsync(Args("--all"), CancellationToken.None));
        Assert.Empty(api.Calls);
        Assert.Contains("MS_TOKEN", error.ToString());
    }
}
