using System.Globalization;

namespace DocumentRelationsGenerator.Cli;

public sealed class CommandLineOptions
{
    public const string Usage = """
        MoySklad document relations generator (JSON API 1.2)

        Usage (from scripts/moysklad-document-relations-generator):
          dotnet run --project DocumentRelationsGenerator -- [options]

        Selection:
          --all                     run every scenario
          --scenario <name>         run one scenario; repeat or use commas for several
          --list-scenarios          print scenarios and the relations they create

        Reproducibility:
          --seed <int>              random seed (printed on every run; default: random)
          --anchor-date <yyyy-MM-dd> date the timeline is built back from (default: today)

        Modes:
          --dry-run                 read reference data only, print plan, dependencies and expected coverage
          --cleanup <manifest.json> delete exactly the objects listed in the manifest (with --dry-run: show plan)

        Other:
          --counterparties <1-5>    test counterparties; every scenario runs for each (default: 1)
          --output-dir <dir>        where run folders are written (default: generated-data)
          --env-file <path>         KEY=VALUE file with MS_* settings (default: ./.env if present)
          --verbose                 print every HTTP method, path and status (never headers)
          --help

        Exit codes: 0 success; 1 a relation/document failed or cleanup incomplete; 2 CLI/config/auth error;
        3 nothing failed but scenarios were skipped for missing prerequisites; 130 interrupted.
        """;

    public bool All { get; private set; }
    public List<string> Scenarios { get; } = [];
    public bool ListScenarios { get; private set; }
    public int? Seed { get; private set; }
    public DateOnly? AnchorDate { get; private set; }
    public bool DryRun { get; private set; }
    public string? CleanupManifest { get; private set; }
    public int Counterparties { get; private set; } = 1;
    public string OutputDirectory { get; private set; } = "generated-data";
    public string? EnvFile { get; private set; }
    public bool Verbose { get; private set; }
    public bool Help { get; private set; }

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new GeneratorException($"{args[i]} requires a value.");

            switch (args[i])
            {
                case "--all": options.All = true; break;
                case "--scenario":
                    options.Scenarios.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--list-scenarios": options.ListScenarios = true; break;
                case "--seed":
                    options.Seed = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) && seed >= 0
                        ? seed
                        : throw new GeneratorException("--seed must be a non-negative integer.");
                    break;
                case "--anchor-date":
                    options.AnchorDate = DateOnly.TryParseExact(Value(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                        ? date
                        : throw new GeneratorException("--anchor-date must be yyyy-MM-dd.");
                    break;
                case "--dry-run": options.DryRun = true; break;
                case "--cleanup": options.CleanupManifest = Value(); break;
                case "--counterparties":
                    options.Counterparties = int.TryParse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                                             count is >= 1 and <= 5
                        ? count
                        : throw new GeneratorException("--counterparties must be between 1 and 5.");
                    break;
                case "--output-dir": options.OutputDirectory = Value(); break;
                case "--env-file": options.EnvFile = Value(); break;
                case "--verbose": options.Verbose = true; break;
                case "--help" or "-h": options.Help = true; break;
                default: throw new GeneratorException($"Unknown argument '{args[i]}'. Use --help.");
            }
        }

        if (options.All && options.Scenarios.Count > 0)
            throw new GeneratorException("Use either --all or --scenario, not both.");
        if (options.CleanupManifest is not null &&
            (options.All || options.Scenarios.Count > 0 || options.Seed is not null || options.AnchorDate is not null))
            throw new GeneratorException("--cleanup cannot be combined with scenario selection, --seed or --anchor-date.");
        if (!options.Help && !options.ListScenarios && options.CleanupManifest is null && !options.DryRun &&
            !options.All && options.Scenarios.Count == 0)
            throw new GeneratorException("Choose what to create: --all or --scenario <name> (or --dry-run / --list-scenarios).");
        return options;
    }
}
