using System.Globalization;
using System.Security.Cryptography;
using DocumentRelationsGenerator.Cleanup;
using DocumentRelationsGenerator.Configuration;
using DocumentRelationsGenerator.Documents;
using DocumentRelationsGenerator.Execution;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.References;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Reporting;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Cli;

public sealed class GeneratorApplication
{
    public delegate IMoySkladApi ApiFactory(GeneratorSettings settings, Action<string>? trace);

    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly ApiFactory apiFactory;
    private readonly Func<DateTimeOffset> now;

    public GeneratorApplication(TextWriter output, TextWriter error, IReadOnlyDictionary<string, string> environment,
        ApiFactory apiFactory, Func<DateTimeOffset> now)
    {
        this.output = output;
        this.error = error;
        this.environment = environment;
        this.apiFactory = apiFactory;
        this.now = now;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            if (options.Help)
            {
                output.WriteLine(CommandLineOptions.Usage);
                return 0;
            }

            if (options.ListScenarios)
            {
                ListScenarios();
                return 0;
            }

            var settings = GeneratorSettings.Load(LoadValues(options));
            return options.CleanupManifest is not null
                ? await CleanupAsync(options, settings, cancellationToken)
                : await GenerateAsync(options, settings, cancellationToken);
        }
        catch (GeneratorException ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (MoySkladApiException ex) when (ex.IsAuthenticationFailure)
        {
            error.WriteLine($"Error: MoySklad rejected the credentials or permissions (HTTP {ex.StatusCode}) on {ex.Method} {ex.Path}. " +
                            "Check MS_TOKEN or MS_LOGIN/MS_PASSWORD; their values are not printed.");
            return 2;
        }
    }

    private IReadOnlyDictionary<string, string> LoadValues(CommandLineOptions options)
    {
        IReadOnlyDictionary<string, string>? file = null;
        if (options.EnvFile is not null)
        {
            if (!File.Exists(options.EnvFile)) throw new GeneratorException($"Env file not found: {options.EnvFile}");
            file = EnvFile.Read(options.EnvFile);
        }
        else if (File.Exists(".env"))
        {
            file = EnvFile.Read(".env");
        }

        return EnvFile.Merge(file, environment);
    }

    private void ListScenarios()
    {
        output.WriteLine($"{ScenarioRegistry.All.Count} scenarios, {RelationCatalog.All.Count} confirmed relations (DOCUMENT_RELATIONS.md)");
        foreach (var scenario in ScenarioRegistry.All)
        {
            output.WriteLine();
            var requirement = scenario.Requirement == ScenarioRequirement.RetailStore ? "  [requires MS_RELTEST_RETAIL_STORE_ID]" : "";
            output.WriteLine($"{scenario.Name}{requirement}");
            output.WriteLine($"  {scenario.Description}");
            foreach (var step in DependencyOrder.Sort(scenario.Steps))
            foreach (var link in step.Links)
                output.WriteLine($"    {step.Key,-32} {RelationCatalog.Get(link.RelationId).Label} <- {link.TargetStep}");
        }
    }

    private async Task<int> GenerateAsync(CommandLineOptions options, GeneratorSettings settings, CancellationToken cancellationToken)
    {
        var seed = options.Seed ?? RandomNumberGenerator.GetInt32(1, 10_000_000);
        var startedAt = now();
        var run = new RunIdentity(RunIdentity.NewRunId(startedAt), seed, options.AnchorDate ?? DateOnly.FromDateTime(startedAt.Date));
        var scenarios = ScenarioRegistry.Select(options.All ? [] : options.Scenarios);
        var plans = scenarios
            .SelectMany(scenario => Enumerable.Range(1, options.Counterparties)
                .Select(index => ScenarioPlanner.Plan(scenario, index, run)))
            .ToList();

        output.WriteLine($"Seed: {seed}");
        output.WriteLine($"Anchor date: {run.AnchorDate:yyyy-MM-dd}");
        output.WriteLine($"Run ID: {run.RunId}{(options.DryRun ? " (not used: dry-run)" : "")}");
        output.WriteLine($"API: {settings.BaseUrl} (auth: {settings.Credentials?.Kind ?? "not configured"})");
        output.WriteLine($"Scenarios: {string.Join(", ", scenarios.Select(scenario => scenario.Name))}");
        output.WriteLine($"Counterparties: {options.Counterparties}");

        if (options.DryRun) return await DryRunAsync(settings, plans, options, cancellationToken);
        if (settings.Credentials is null)
            throw new GeneratorException("Set MS_TOKEN or MS_LOGIN and MS_PASSWORD (environment, .env or --env-file).");

        var api = apiFactory(settings, options.Verbose ? line => output.WriteLine($"    http: {line}") : null);
        try
        {
            return await ExecuteAsync(api, settings, run, options, scenarios, plans, startedAt, cancellationToken);
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
    }

    private async Task<int> ExecuteAsync(IMoySkladApi api, GeneratorSettings settings, RunIdentity run, CommandLineOptions options,
        IReadOnlyList<ScenarioDefinition> scenarios, IReadOnlyList<ScenarioPlan> plans, DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(options.OutputDirectory, run.RunId, "manifest.json");
        var store = new ManifestStore(manifestPath, new RunManifest
        {
            RunId = run.RunId,
            Seed = run.Seed,
            AnchorDate = run.AnchorDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StartedAt = startedAt,
            BaseUrl = settings.BaseUrl.ToString(),
            CounterpartyCount = options.Counterparties,
            SelectedScenarios = scenarios.Select(scenario => scenario.Name).ToList()
        });
        store.Save();
        output.WriteLine($"Manifest: {manifestPath}");

        try
        {
            output.WriteLine();
            output.WriteLine("Reference data:");
            var provider = new ReferenceDataProvider(api, settings, output);
            var refs = await provider.LoadExistingAsync(cancellationToken);
            PrintReferenceData(refs);
            var types = scenarios.SelectMany(scenario => scenario.Steps).Select(step => step.DocumentType).ToHashSet();
            await provider.PrepareTestEntitiesAsync(refs, run, options.Counterparties,
                needsExpenseItem: types.Overlaps(["paymentout", "cashout"]),
                needsCommissionContract: types.Overlaps(["commissionreportin", "commissionreportout"]),
                store, cancellationToken);

            var runner = new ScenarioRunner(api, new DocumentFactory(refs, run), refs, store, output);
            foreach (var plan in plans)
            {
                if (plan.Scenario.Requirement == ScenarioRequirement.RetailStore && refs.RetailProblem is not null)
                {
                    var skipped = ScenarioRunner.NewResult(plan);
                    skipped.Status = "skipped";
                    skipped.SkipReason = refs.RetailProblem;
                    store.Manifest.Scenarios.Add(skipped);
                    store.Save();
                    output.WriteLine();
                    output.WriteLine($"=== {plan.Label}: SKIPPED ({refs.RetailProblem})");
                    continue;
                }

                await runner.RunAsync(plan, cancellationToken);
            }

            var summary = CoverageCalculator.Calculate(RelationCatalog.All, store.Manifest.Scenarios);
            CoverageCalculator.Print(summary, output);
            var exitCode = CoverageCalculator.ExitCode(summary, store.Manifest.Scenarios);
            store.Manifest.Coverage = summary.ToManifest();
            store.Manifest.Status = exitCode == 1 ? "failed" : "completed";
            store.Manifest.FinishedAt = now();
            store.Save();
            PrintAggregate(store.Manifest, run, options, manifestPath);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Finish(store, "interrupted");
            error.WriteLine($"Interrupted. Objects created so far are in {manifestPath}; remove them with --cleanup.");
            return 130;
        }
        catch (MoySkladApiException ex) when (!ex.IsAuthenticationFailure)
        {
            Finish(store, "failed");
            error.WriteLine($"Error while preparing reference data: {ex.Method} {ex.Path} status={ex.StatusCode?.ToString() ?? "-"} " +
                            $"code={ex.ErrorCode?.ToString() ?? "-"} message={ex.ErrorMessage ?? "-"}");
            error.WriteLine($"Created objects are listed in {manifestPath}; remove them with --cleanup.");
            return 1;
        }
        catch
        {
            Finish(store, "failed");
            throw;
        }
    }

    private void Finish(ManifestStore store, string status)
    {
        store.Manifest.Status = status;
        store.Manifest.FinishedAt = now();
        store.Save();
    }

    private async Task<int> DryRunAsync(GeneratorSettings settings, IReadOnlyList<ScenarioPlan> plans, CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        output.WriteLine();
        output.WriteLine("DRY-RUN: only GET requests; nothing is created, changed or deleted.");
        string? retailProblem = settings.RetailStoreId is null
            ? "MS_RELTEST_RETAIL_STORE_ID is not set"
            : null;
        if (settings.Credentials is null)
        {
            output.WriteLine("Credentials are not configured: reference data is not checked (offline plan).");
        }
        else
        {
            var api = apiFactory(settings, options.Verbose ? line => output.WriteLine($"    http: {line}") : null);
            try
            {
                output.WriteLine("Reference data (read-only):");
                var refs = await new ReferenceDataProvider(api, settings, output).LoadExistingAsync(cancellationToken);
                PrintReferenceData(refs);
                retailProblem = refs.RetailProblem;
            }
            finally
            {
                (api as IDisposable)?.Dispose();
            }
        }

        var types = plans.SelectMany(plan => plan.Scenario.Steps).Select(step => step.DocumentType).ToHashSet();
        output.WriteLine();
        output.WriteLine("Test entities a real run creates (prefix MSCONTRACTOR-RELTEST-, run ID in every name):");
        output.WriteLine($"  project 1, products {ReferenceData.ProductCount}, services {ReferenceData.ServiceCount}, " +
                         $"counterparties {options.Counterparties} (with {ReferenceData.AccountsPerCounterparty} accounts each), " +
                         $"sales contracts {options.Counterparties}" +
                         (types.Overlaps(["commissionreportin", "commissionreportout"]) ? $", commission contracts {options.Counterparties}" : "") +
                         "; store/expense item only if the account has none");

        var planned = new List<string>();
        var skipped = new List<string>();
        foreach (var plan in plans)
        {
            var skipReason = plan.Scenario.Requirement == ScenarioRequirement.RetailStore ? retailProblem : null;
            var relations = plan.Steps.SelectMany(step => step.Step.Links).Select(link => link.RelationId).ToList();
            (skipReason is null ? planned : skipped).AddRange(relations);
            PrintPlan(plan, skipReason);
        }

        var summary = CoverageCalculator.Expected(RelationCatalog.All, planned, skipped);
        CoverageCalculator.Print(summary, output, dryRun: true);
        output.WriteLine("Nothing was created (dry-run).");
        return 0;
    }

    private void PrintPlan(ScenarioPlan plan, string? skipReason)
    {
        output.WriteLine();
        output.WriteLine($"=== {plan.Label}{(skipReason is null ? "" : $"  SKIPPED: {skipReason}")}");
        output.WriteLine($"    contract={(plan.WithContract ? "with" : "without")}, agentAccount={(plan.WithAgentAccount ? "with" : "without")}" +
                         (plan.WithForeignCurrency ? ", foreign currency if the account has one" : ""));
        foreach (var step in plan.Steps)
        {
            var details = new List<string> { MoySkladTime.Format(step.Moment)[..16] };
            if (DocumentFieldSupport.Supports(step.Step.DocumentType, "applicable"))
                details.Add(step.Applicable ? "applicable" : "draft");
            details.Add(step.Step.Kind.ToString().ToLowerInvariant());
            if (step.Positions.Count > 0)
                details.Add(string.Join(", ", step.Positions.Select(position =>
                    $"{(position.IsService ? "SERVICE" : "PRODUCT")}-{position.AssortmentIndex + 1:00} x{position.Quantity.ToString(CultureInfo.InvariantCulture)} " +
                    $"@{(position.PriceKopecks / 100m).ToString("0.00", CultureInfo.InvariantCulture)} RUB" +
                    (position.Discount > 0 ? $" -{position.Discount}%" : ""))));
            if (step.Step.Links.Any(link => RelationCatalog.Get(link.RelationId).Kind == RelationKind.Operation))
                details.Add($"payment share {step.PaymentShare:P0}");
            output.WriteLine($"  {step.Index + 1,2}. {step.Step.Key,-32} {step.Step.DocumentType,-20} {string.Join("; ", details)}");
            foreach (var link in step.Step.Links)
                output.WriteLine($"        depends on {link.TargetStep}: {RelationCatalog.Get(link.RelationId).Label}");
        }
    }

    private void PrintReferenceData(ReferenceData refs)
    {
        output.WriteLine($"  organization: {refs.Organization["name"]} (existing, reused)");
        output.WriteLine($"  store: {(refs.Store is null ? "none found, a test store will be created" : $"{refs.Store["name"]} (existing, reused)")}");
        output.WriteLine($"  organization accounts: {refs.OrganizationAccounts.Count}");
        output.WriteLine($"  VAT rates: {(refs.VatRates.Count == 0 ? "none available, documents without VAT" : string.Join(", ", refs.VatRates))}");
        output.WriteLine($"  foreign currency: {refs.ForeignCurrency?["name"]?.ToString() ?? "none"}");
        output.WriteLine($"  expense item: {refs.ExpenseItem?["name"]?.ToString() ?? "none found, a test item will be created if needed"}");
        output.WriteLine($"  employee (owner): {refs.Employee?["name"]?.ToString() ?? "unavailable"}");
        output.WriteLine($"  retail: {(refs.RetailProblem is null ? $"{refs.RetailStore?["name"]} (existing, a test shift will be opened)" : $"retail-flow skipped: {refs.RetailProblem}")}");
    }

    private void PrintAggregate(RunManifest manifest, RunIdentity run, CommandLineOptions options, string manifestPath)
    {
        output.WriteLine();
        output.WriteLine("Scenarios:");
        foreach (var scenario in manifest.Scenarios)
        {
            output.WriteLine($"  {scenario.Status.ToUpperInvariant(),-8} {scenario.Name} [counterparty {scenario.CounterpartyIndex}]" +
                             $" documents={scenario.Documents.Count} errors={scenario.Errors.Count} warnings={scenario.Warnings.Count}" +
                             (scenario.SkipReason is null ? "" : $" ({scenario.SkipReason})"));
            foreach (var failure in scenario.Errors)
                output.WriteLine($"           scenario={failure.Scenario} step={failure.Step} type={failure.DocumentType} " +
                                 $"operation={failure.Operation} status={failure.HttpStatus?.ToString() ?? "-"} " +
                                 $"code={failure.ErrorCode?.ToString() ?? "-"} message={failure.Message ?? "-"}");
        }

        var failed = manifest.Scenarios.Where(scenario => scenario.Status == "failed").Select(scenario => scenario.Name).Distinct().ToList();
        if (failed.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Reproduce failed scenarios:");
            output.WriteLine($"  dotnet run --project DocumentRelationsGenerator -- --seed {run.Seed} --anchor-date {run.AnchorDate:yyyy-MM-dd} " +
                             $"--counterparties {options.Counterparties} --scenario {string.Join(",", failed)}");
        }

        output.WriteLine();
        output.WriteLine($"Seed: {run.Seed}");
        output.WriteLine($"Manifest: {manifestPath}");
        output.WriteLine($"Cleanup:  dotnet run --project DocumentRelationsGenerator -- --cleanup {manifestPath}");
    }

    private async Task<int> CleanupAsync(CommandLineOptions options, GeneratorSettings settings, CancellationToken cancellationToken)
    {
        var manifest = ManifestStore.Load(options.CleanupManifest!);
        var plan = CleanupPlanner.Plan(manifest);
        output.WriteLine($"Cleanup of run {manifest.RunId}: {plan.Count} object(s) from {options.CleanupManifest}");
        if (options.DryRun)
        {
            foreach (var item in plan)
                output.WriteLine($"  would delete {item.Kind,-8} {item.Type,-20} {item.Name} ({item.Id})");
            output.WriteLine("Nothing was deleted (dry-run).");
            return 0;
        }

        if (settings.Credentials is null)
            throw new GeneratorException("Set MS_TOKEN or MS_LOGIN and MS_PASSWORD (environment, .env or --env-file).");
        if (!Uri.TryCreate(manifest.BaseUrl, UriKind.Absolute, out var manifestApi) ||
            !string.Equals(manifestApi.Authority, settings.BaseUrl.Authority, StringComparison.OrdinalIgnoreCase))
            throw new GeneratorException("The manifest was created against another API host; refusing to delete.");

        var api = apiFactory(settings, options.Verbose ? line => output.WriteLine($"    http: {line}") : null);
        try
        {
            var report = await new CleanupRunner(api, output).RunAsync(manifest, options.CleanupManifest!, now, cancellationToken);
            CleanupRunner.Print(report, output);
            var reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.CleanupManifest!))!,
                $"cleanup-report-{now():yyyyMMdd-HHmmss}.json");
            ManifestStore.WriteJsonAtomically(reportPath, CleanupRunner.Serialize(report));
            output.WriteLine($"Cleanup report: {reportPath}");
            return report.Complete ? 0 : 1;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
    }
}
